import { describe, expect, it } from 'vitest'
import type { LobbyRoster } from './api'
import {
  LOBBY_ROSTER_POLL_INTERVAL_MS, LOBBY_ROSTER_MAX_BACKOFF_MS,
  classifyRosterPollFailure, reconcilePolledRoster, rosterPollDelayMs,
} from './rosterSync'

const lobbyId = 'room-' + 'a'.repeat(32)
const otherId = 'room-' + 'b'.repeat(32)
const alice = { playerId: 'guest-alice', teamId: 'white', heroId: null }
const bob = { playerId: 'guest-bob', teamId: 'black', heroId: 'kira' }

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
    )).toBe('unchanged')
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
    [{ ...roster(2), participants: [{ playerId: 'guest-alice' }] }],
  ])('ignores malformed responses without changing or revoking local state', payload => {
    expect(reconcilePolledRoster(roster(1), payload as LobbyRoster, alice.playerId))
      .toBe('unchanged')
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
