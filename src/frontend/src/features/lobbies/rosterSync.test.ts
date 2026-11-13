import { describe, expect, it } from 'vitest'
import { heroRoster } from '@/features/heroes/catalog'
import type { LobbyRoster } from './api'
import {
  LOBBY_ROSTER_POLL_INTERVAL_MS, LOBBY_ROSTER_MAX_BACKOFF_MS,
  classifyConflictRosterRefresh, classifyRosterPollFailure,
  isVerifiedLobbyRoster, reconcilePolledRoster, rosterPollDelayMs,
} from './rosterSync'

const lobbyId = 'room-' + 'a'.repeat(32)
const otherId = 'room-' + 'b'.repeat(32)
const alice = { playerId: 'guest-' + '1'.repeat(32), teamId: 'white', heroId: null }
const bob = { playerId: 'guest-' + '2'.repeat(32), teamId: 'black', heroId: 'kira' }

function roster(revision: number, participants = [alice, bob] as LobbyRoster['participants']): LobbyRoster {
  return { lobbyId, revision, teamIds: ['white', 'black'], participants }
}

describe('pre-match authenticated lobby polling', () => {
  it('has a fixed, low-frequency interval and accepts newer authorized revisions', () => {
    expect(LOBBY_ROSTER_POLL_INTERVAL_MS).toBe(15_000)
    const current = roster(3)
    const next = roster(4, [alice, { ...bob, heroId: 'shilla' }])
    expect(reconcilePolledRoster(current, next, alice.playerId)).toBe('updated')
    expect(current.revision).toBe(3)
    expect(current.participants[1].heroId).toBe('kira')
  })

  it('never regresses revision or replaces the same version', () => {
    expect(reconcilePolledRoster(roster(7), roster(6), alice.playerId)).toBe('unchanged')
    expect(reconcilePolledRoster(roster(7), roster(7), alice.playerId)).toBe('unchanged')
    expect(reconcilePolledRoster(roster(7), roster(8, [bob]), alice.playerId)).toBe('missing')
    // An outdated response must not remove a member if a newer revision is cached.
    expect(reconcilePolledRoster(roster(9), roster(8, [bob]), alice.playerId)).toBe('unchanged')
  })

  it('rejects rosters for another lobby regardless of revision or participants', () => {
    expect(reconcilePolledRoster(
      roster(1), { ...roster(100), lobbyId: otherId }, alice.playerId,
    )).toBe('invalid')
  })

  it('treats an authorized 200 that lost actor membership as missing, not public roster', () => {
    expect(reconcilePolledRoster(roster(1), roster(2, [bob]), alice.playerId)).toBe('missing')
    expect(reconcilePolledRoster(roster(1), roster(1, [bob]), alice.playerId)).toBe('missing')
  })

  it.each([
    [null],
    [{ ...roster(2), revision: Number.NaN }],
    [{ ...roster(2), revision: Infinity }],
    [{ ...roster(2), revision: 1.5 }],
    [{ ...roster(2), teamIds: null }],
    [{ ...roster(2), participants: null }],
    [{ ...roster(2), participants: [null] }],
    [{ ...roster(2), participants: [{ playerId: alice.playerId }] }],
  ])('classifies malformed responses separately without authorizing room changes', payload => {
    expect(reconcilePolledRoster(roster(1), payload as LobbyRoster, alice.playerId))
      .toBe('invalid')
  })

  it.each([
    ['unknown private field', { ...roster(3), secretRoute: 'hidden-path' }],
    ['unknown player field', { ...roster(3), participants: [{ ...alice, destination: 'hidden-path' }, bob] }],
    ['duplicate player', roster(3, [alice, alice])],
    ['duplicate team', { ...roster(3), teamIds: ['white', 'white'] }],
    ['unknown player team', roster(3, [{ ...alice, teamId: 'unknown' }, bob])],
    ['unknown hero for other member', roster(3, [alice, { ...bob, heroId: 'wanderer' }])],
    ['uppercase hero for other member', roster(3, [alice, { ...bob, heroId: 'Kira' }])],
    ['huge hero for other member', roster(3, [alice, { ...bob, heroId: 'kira'.repeat(100_000) }])],
    ['non-string hero for other member', { ...roster(3),
      participants: [alice, { ...bob, heroId: 17 }] }],
    ['legacy noncanonical guest ID', roster(3, [alice, { ...bob, playerId: 'guest-bob' }])],
    ['uppercase guest hex', roster(3, [alice, { ...bob, playerId: 'guest-' + 'F'.repeat(32) }])],
    ['truncated guest ID', roster(3, [alice, { ...bob, playerId: 'guest-' + 'f'.repeat(31) }])],
    ['oversized guest ID', roster(3, [alice, { ...bob, playerId: 'guest-' + 'f'.repeat(33) }])],
    ['non-string guest ID', { ...roster(3), participants: [alice, { ...bob, playerId: { id: bob.playerId } }] }],
    ['invalid actor player ID', roster(3, [{ ...alice, playerId: 'guest-alice' }, bob])],
    ['empty roster', roster(3, [])],
    ['too many members', roster(3, Array.from({ length: 17 }, (_, i) =>
      ({ playerId: 'guest-' + i.toString(16).padStart(32, '0'), teamId: 'white', heroId: null })))],
    ['invalid room ID', { ...roster(3), lobbyId: 'room-INVALID' }],
    ['additional field and missing actor', { ...roster(3, [bob]), secretRoute: 'hidden' }],
  ])('flags passive 200 with %s for backoff without changing the room', (_case, incoming) => {
    expect(reconcilePolledRoster(roster(2), incoming as LobbyRoster, alice.playerId))
      .toBe('invalid')
  })

  it('distinguishes invalid payloads from valid stale/no-change and missing-member responses', () => {
    const current = roster(7)
    expect(reconcilePolledRoster(current, { ...roster(8), secretRoute: 'hidden' } as LobbyRoster, alice.playerId))
      .toBe('invalid')
    expect(reconcilePolledRoster(current, roster(6), alice.playerId)).toBe('unchanged')
    expect(reconcilePolledRoster(current, roster(7), alice.playerId)).toBe('unchanged')
    expect(reconcilePolledRoster(current, roster(7, [bob]), alice.playerId)).toBe('missing')
    expect(reconcilePolledRoster(current, roster(6, [bob]), alice.playerId)).toBe('unchanged')
    expect(reconcilePolledRoster(current, roster(8), alice.playerId)).toBe('updated')
    // Scheduler maps repeated invalid DTOs to the same capped delays
    // already used for 503/unreachable Redis, rather than the healthy 15s.
    expect(rosterPollDelayMs(1, 0)).toBe(30_000)
    expect(rosterPollDelayMs(2, 0)).toBe(60_000)
    expect(rosterPollDelayMs(3, 0)).toBe(120_000)
  })

  it('backs off on transient errors, caps retry delay and resets after success', () => {
    expect(rosterPollDelayMs(0, 0)).toBe(15_000)
    expect(rosterPollDelayMs(1, 0)).toBe(30_000)
    expect(rosterPollDelayMs(2, 0)).toBe(60_000)
    expect(rosterPollDelayMs(3, 0)).toBe(120_000)
    expect(rosterPollDelayMs(4, 0)).toBe(120_000)
    expect(rosterPollDelayMs(1000, 0)).toBe(120_000)
    expect(LOBBY_ROSTER_MAX_BACKOFF_MS).toBe(120_000)
    // UI resets consecutiveFailures to zero for 200 and 304.
    expect(rosterPollDelayMs(0, 1)).toBe(15_000)
  })

  it('adds only bounded jitter without retry storms or overflow', () => {
    expect(rosterPollDelayMs(1, 0.5)).toBe(33_000)
    expect(rosterPollDelayMs(1, 1)).toBe(36_000)
    expect(rosterPollDelayMs(2, 1)).toBe(72_000)
    expect(rosterPollDelayMs(3, 1)).toBe(120_000)
    expect(rosterPollDelayMs(5, 0.5)).toBe(120_000)
  })

  it.each([-1, 0.25, NaN, Infinity, -Infinity])(
    'treats invalid failure counts (%s) as normal cadence', failures => {
      expect(rosterPollDelayMs(failures, 1)).toBe(15_000)
    },
  )

  it.each([-1, 2, NaN, Infinity, -Infinity])(
    'clamps invalid random fractions (%s) to a safe jitter window', fraction => {
      const delay = rosterPollDelayMs(1, fraction)
      expect(delay).toBeGreaterThanOrEqual(30_000)
      expect(delay).toBeLessThanOrEqual(36_000)
    },
  )

  it('invalidates only on 401 or 404, not on Redis/edge outages', () => {
    expect(classifyRosterPollFailure(401)).toBe('expired')
    expect(classifyRosterPollFailure(404)).toBe('missing')
    for (const code of [undefined, 0, 400, 403, 409, 429, 502, 503, 504]) {
      expect(classifyRosterPollFailure(code)).toBe('retain')
    }
  })
})

describe('explicit authenticated roster adoption (#110)', () => {
  it('accepts a valid member-scoped roster with correct room and revision', () => {
    expect(isVerifiedLobbyRoster(roster(4), alice.playerId, lobbyId, 4)).toBe(true)
    expect(isVerifiedLobbyRoster(roster(4), bob.playerId)).toBe(true)
  })

  it.each([
    ['null', null],
    ['array', []],
    ['wrong room', { ...roster(8), lobbyId: otherId }],
    ['invalid invite', { ...roster(8), lobbyId: 'room-injected' }],
    ['fractional revision', { ...roster(8), revision: 1.5 }],
    ['negative revision', { ...roster(8), revision: -1 }],
    ['single team', { ...roster(8), teamIds: ['white'] }],
    ['duplicate team', { ...roster(8), teamIds: ['white', 'white'] }],
    ['missing actor', roster(8, [bob])],
    ['duplicate player', roster(8, [alice, alice])],
    ['unknown team', roster(8, [{ ...alice, teamId: 'other' }])],
    ['unknown hero ID', roster(8, [alice, { ...bob, heroId: 'wanderer' }])],
    ['uppercase hero ID', roster(8, [alice, { ...bob, heroId: 'Kira' }])],
    ['empty hero ID', roster(8, [alice, { ...bob, heroId: '' }])],
    ['huge hero ID', roster(8, [alice, { ...bob, heroId: 'kira'.repeat(100_000) }])],
    ['numeric hero ID', { ...roster(8), participants: [alice, { ...bob, heroId: 12 }] }],
    ['hero ID object', { ...roster(8), participants: [alice, { ...bob, heroId: { id: 'kira' } }] }],
    ['legacy participant ID', roster(8, [alice, { ...bob, playerId: 'guest-bob' }])],
    ['uppercase participant ID', roster(8, [alice, { ...bob, playerId: 'guest-' + 'A'.repeat(32) }])],
    ['arbitrary participant ID', roster(8, [alice, { ...bob, playerId: 'other-account' }])],
    ['huge participant ID', roster(8, [alice, { ...bob, playerId: 'guest-' + 'a'.repeat(100_000) }])],
    ['null participant', { ...roster(8), participants: [null] }],
    ['missing hero', { ...roster(8), participants: [{ playerId: alice.playerId, teamId: 'white' }] }],
    ['extra private field', { ...roster(8), secretRoute: 'hidden' }],
    ['extra player field', { ...roster(8), participants: [{ ...alice, destination: 'hidden' }] }],
    ['too many members', roster(8, Array.from({ length: 17 }, (_, i) =>
      ({ playerId: 'guest-' + i.toString(16).padStart(32, '0'), teamId: 'white', heroId: null })))],
  ])('rejects %s rather than importing an invalid 200', (_kind, payload) => {
    expect(isVerifiedLobbyRoster(payload, alice.playerId, lobbyId, 1)).toBe(false)
  })

  it('accepts canonical IDs for every participant and rejects even one mismatched member', () => {
    const current = roster(3)
    const next = roster(4, [alice, bob, {
      playerId: 'guest-' + '3'.repeat(32), teamId: 'black', heroId: null,
    }])
    expect(isVerifiedLobbyRoster(next, alice.playerId, lobbyId, 3)).toBe(true)
    expect(reconcilePolledRoster(current, next, alice.playerId)).toBe('updated')
    const noncanonical = { ...next, participants: [
      alice, bob, { playerId: 'guest-long-and-fake', teamId: 'black', heroId: null },
    ] }
    expect(isVerifiedLobbyRoster(noncanonical, alice.playerId, lobbyId, 3)).toBe(false)
    expect(reconcilePolledRoster(current, noncanonical, alice.playerId)).toBe('invalid')
  })

  it('does not accept wrong requested room, stale revision or other identity', () => {
    expect(isVerifiedLobbyRoster(roster(4), alice.playerId, otherId)).toBe(false)
    expect(isVerifiedLobbyRoster(roster(4), alice.playerId, lobbyId, 5)).toBe(false)
    expect(isVerifiedLobbyRoster(roster(4), 'guest-unknown')).toBe(false)
  })
})


describe('canonical hero IDs across roster adoption (#117)', () => {
  it.each(heroRoster.map(hero => hero.id))('accepts server-known hero %s from shared roster', heroId => {
    const next = roster(4, [alice, { ...bob, heroId }])
    expect(isVerifiedLobbyRoster(next, alice.playerId, lobbyId, 3)).toBe(true)
    expect(reconcilePolledRoster(roster(3), next, alice.playerId)).toBe('updated')
    expect(classifyConflictRosterRefresh(roster(3), next, alice.playerId))
      .toBe('verified')
  })

  it('accepts unselected null hero and does not invent gameplay constraints', () => {
    const next = roster(4, [alice, { ...bob, heroId: null }])
    expect(isVerifiedLobbyRoster(next, alice.playerId, lobbyId, 3)).toBe(true)
    expect(reconcilePolledRoster(roster(3), next, alice.playerId)).toBe('updated')
  })

  it('rejects a corrupt hero even when the actor is missing; not a revocation', () => {
    const lost = roster(4, [{ ...bob, heroId: 'not-registered' }])
    expect(reconcilePolledRoster(roster(3), lost, alice.playerId)).toBe('invalid')
    expect(classifyConflictRosterRefresh(roster(3), lost, alice.playerId)).toBe('invalid')
  })
})



describe('immutable team IDs for an existing lobby (#119)', () => {
  it.each([
    ['reordered', { ...roster(4), teamIds: ['black', 'white'] }],
    ['renamed with consistent members', { ...roster(4),
      teamIds: ['white', 'gold'],
      participants: [alice, { ...bob, teamId: 'gold' }] }],
    ['extra valid team', { ...roster(4), teamIds: ['white', 'black', 'gold'] }],
  ])('rejects %s teams before adopting any valid-looking 200', (_kind, incoming) => {
    const current = roster(3)
    // Without an established room, the new definition is a legal custom
    // configuration; only a change to the SAME room is forbidden.
    expect(isVerifiedLobbyRoster(incoming, alice.playerId, lobbyId, 3)).toBe(true)
    expect(isVerifiedLobbyRoster(
      incoming, alice.playerId, lobbyId, 3, current.teamIds,
    )).toBe(false)
    expect(reconcilePolledRoster(current, incoming, alice.playerId)).toBe('invalid')
    expect(classifyConflictRosterRefresh(current, incoming, alice.playerId))
      .toBe('invalid')
  })

  it('rejects removal from a three-team room even with a valid two-team DTO', () => {
    const current = { ...roster(3), teamIds: ['white', 'black', 'gold'] }
    const incoming = roster(4)
    expect(isVerifiedLobbyRoster(
      incoming, alice.playerId, lobbyId, current.revision, current.teamIds,
    )).toBe(false)
    expect(reconcilePolledRoster(current, incoming, alice.playerId)).toBe('invalid')
    expect(classifyConflictRosterRefresh(current, incoming, alice.playerId))
      .toBe('invalid')
  })

  it('does not treat forged missing membership with changed teams as revocation', () => {
    const current = roster(3)
    const missing = { ...roster(4, [bob]),
      teamIds: ['white', 'gold'],
      participants: [{ ...bob, teamId: 'gold' }],
    }
    expect(classifyConflictRosterRefresh(current, missing, alice.playerId))
      .toBe('invalid')
    expect(reconcilePolledRoster(current, missing, alice.playerId))
      .toBe('invalid')
  })

  it('allows unchanged teams after ordinary revisions and custom first-time joins', () => {
    const current = { ...roster(4), teamIds: ['white', 'black', 'gold'] }
    const updated = { ...roster(5, [alice, { ...bob, teamId: 'gold' }]),
      teamIds: [...current.teamIds] }
    expect(isVerifiedLobbyRoster(
      updated, alice.playerId, lobbyId, current.revision, current.teamIds,
    )).toBe(true)
    expect(reconcilePolledRoster(current, updated, alice.playerId)).toBe('updated')
    expect(classifyConflictRosterRefresh(current, updated, alice.playerId))
      .toBe('verified')

    const firstRoom = { ...updated, lobbyId: otherId, revision: 1 }
    expect(isVerifiedLobbyRoster(firstRoom, alice.playerId, otherId)).toBe(true)
    expect(isVerifiedLobbyRoster(firstRoom, alice.playerId, lobbyId)).toBe(false)
  })
})

describe('409 conflict refresh requires validated membership proof (#115)', () => {
  it('adopts only current, same-room, strictly verified member rosters', () => {
    const current = roster(4)
    expect(classifyConflictRosterRefresh(current, roster(4), alice.playerId))
      .toBe('verified')
    expect(classifyConflictRosterRefresh(current, roster(5, [alice]), alice.playerId))
      .toBe('verified')
    expect(classifyConflictRosterRefresh(current, roster(5, [bob]), alice.playerId))
      .toBe('missing')
    // A valid equal revision with confirmed membership loss still proves
    // absence, but an obsolete revision cannot revoke a newer local view.
    expect(classifyConflictRosterRefresh(current, roster(4, [bob]), alice.playerId))
      .toBe('missing')
    expect(classifyConflictRosterRefresh(current, roster(3, [bob]), alice.playerId))
      .toBe('invalid')
  })

  it.each([
    ['null JSON', null],
    ['array JSON', []],
    ['foreign room', { ...roster(5, [bob]), lobbyId: otherId }],
    ['stale room', roster(3, [bob])],
    ['empty roster', roster(5, [])],
    ['missing actor with extra private field', { ...roster(5, [bob]), secretRoute: 'hidden' }],
    ['malformed participant', { ...roster(5), participants: [null] }],
    ['noncanonical participant', roster(5, [alice, { ...bob, playerId: 'guest-bob' }])],
    ['duplicate participant', roster(5, [bob, bob])],
    ['missing team', { ...roster(5), teamIds: ['white'] }],
    ['malformed revision', { ...roster(5, [bob]), revision: 1.5 }],
    ['unexpected participant field', { ...roster(5),
      participants: [alice, { ...bob, secretRoute: 'hidden' }] }],
  ])('does not infer revocation after 409 from %s', (_kind, incoming) => {
    expect(classifyConflictRosterRefresh(roster(4), incoming, alice.playerId))
      .toBe('invalid')
  })

  it('rejects invalid local actor IDs without trusting server-looking JSON', () => {
    expect(classifyConflictRosterRefresh(roster(4), roster(5, [bob]), 'guest-alice'))
      .toBe('invalid')
  })
})
