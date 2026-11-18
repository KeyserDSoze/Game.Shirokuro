import { getHeroById } from '@/features/heroes/catalog'
import { isCanonicalGuestPlayerId, type LobbyRoster } from './api'

/** Read-only, member-scoped, pre-match synchronization; not match state. */
export const LOBBY_ROSTER_POLL_INTERVAL_MS = 15_000
export const LOBBY_ROSTER_MAX_BACKOFF_MS = 120_000
// A network/visibility hint may bring forward one passive read, but may
// never turn a failing 120-second backoff into a request storm.
export const LOBBY_ROSTER_RESUME_HINT_COOLDOWN_MS = 30_000

/**
 * Minimum interval from the last read attempt (or initial room display) to
 * a wake-triggered read. Monotonic performance.now() is supplied by the UI:
 * a backwards/invalid clock must fail to the safe 15-second floor.
 */
export function rosterResumeDelayMs(lastReadStartedAt: number, now: number): number {
  if (!Number.isFinite(lastReadStartedAt) || !Number.isFinite(now) ||
      now < lastReadStartedAt) return LOBBY_ROSTER_POLL_INTERVAL_MS
  return Math.max(0, LOBBY_ROSTER_POLL_INTERVAL_MS - (now - lastReadStartedAt))
}

/** One accepted hint per 30 seconds; duplicate focus/online events coalesce. */
export function rosterCanHonorResumeHint(
  lastHintAt: number | null, now: number,
): boolean {
  return Number.isFinite(now) && (lastHintAt === null ||
    (Number.isFinite(lastHintAt) &&
     now - lastHintAt >= LOBBY_ROSTER_RESUME_HINT_COOLDOWN_MS))
}

/**
 * Bounded exponential retry with positive 0..20% jitter (where headroom
 * permits). No delay below 15s and never more than 120s. Pure for testing;
 * the UI supplies Math.random() instead of sharing any session identifiers.
 * This limits API/Redis load during outages; it is not a match timer.
 */
export function rosterPollDelayMs(
  consecutiveFailures: number, randomFraction: number,
): number {
  if (!Number.isSafeInteger(consecutiveFailures) || consecutiveFailures <= 0)
    return LOBBY_ROSTER_POLL_INTERVAL_MS
  const base = consecutiveFailures === 1 ? 30_000
    : consecutiveFailures === 2 ? 60_000
    : LOBBY_ROSTER_MAX_BACKOFF_MS
  const fraction = Number.isFinite(randomFraction)
    ? Math.max(0, Math.min(1, randomFraction)) : 0
  return Math.min(LOBBY_ROSTER_MAX_BACKOFF_MS,
    Math.round(base * (1 + fraction * 0.2)))
}

/**
 * A failing roster read can be advanced by one online/focus hint so a real
 * network recovery is noticed promptly. Once that early read also fails,
 * additional hints must NOT defeat the normal 30/60/120s backoff. An
 * obsolete but syntactically valid 200 is not verified recovery and cannot
 * replenish this budget; nor can an inconsistent 0-failures/used flag.
 * The caller resets the allowance only after an authorized current/newer
 * 200 or 304, or when the room/guest changes. Pure policy: no clock or mutation side effects.
 */
export function rosterCanAdvanceOnWake(
  consecutiveFailures: number, outageWakeUsed: boolean,
): boolean {
  return Number.isSafeInteger(consecutiveFailures) &&
    consecutiveFailures >= 0 &&
    !outageWakeUsed
}

export type PolledRosterDecision = 'updated' | 'unchanged' | 'missing' | 'invalid'

/**
 * The last member-authorized room snapshot can remain visible through
 * network/Redis outages. Freshness is a claim about the *last verification*,
 * not a grant of membership or permission to mutate the room.
 */
export type RosterSyncStatus = 'current' | 'stale'
export type RosterSyncEvidence =
  | 'verified' | 'outage' | 'invalid' | 'older' | 'paused'

export function nextRosterSyncStatus(
  current: RosterSyncStatus, evidence: RosterSyncEvidence,
): RosterSyncStatus {
  switch (evidence) {
    case 'verified':
      // A member-authorized 304 or fully validated same/newer 200 is
      // sufficient; a stale 200 from a lagging replica is NOT.
      return 'current'
    case 'outage':
    case 'invalid':
    case 'older':
      return 'stale'
    case 'paused':
      // Hidden tabs, aborted polls and pending user actions are not
      // server responses and cannot prove either loss or recovery.
      return current
  }
}
export type PollFailureDecision = 'expired' | 'missing' | 'retain'
export type ConflictRosterRefreshDecision = 'verified' | 'missing' | 'invalid'

/**
 * LobbyState is immutable: an idempotent action returns the exact current
 * snapshot, while every real join/leave/hero/team edit increments revision.
 * A same-room, same-revision 200 with *different* members cannot be
 * authoritative, including changed ordering or opponent hero selection.
 * Call this only after the incoming DTO has passed strict shape validation.
 */
function isSameRosterSnapshot(current: LobbyRoster, incoming: LobbyRoster): boolean {
  return incoming.lobbyId === current.lobbyId &&
    incoming.revision === current.revision &&
    current.teamIds.length === incoming.teamIds.length &&
    current.teamIds.every((team, index) => team === incoming.teamIds[index]) &&
    current.participants.length === incoming.participants.length &&
    current.participants.every((member, index) => {
      const next = incoming.participants[index]
      return member.playerId === next.playerId &&
        member.teamId === next.teamId && member.heroId === next.heroId
    })
}

/**
 * A 409 may be followed by a 200 that is malformed, foreign, or stale.
 * Only a fully verified room/revision/participant DTO can establish that
 * the actor really lost membership. Never examine unvalidated participants.
 */
export function classifyConflictRosterRefresh(
  current: LobbyRoster, incoming: unknown, actorId: string,
): ConflictRosterRefreshDecision {
  if (!isCanonicalGuestPlayerId(actorId) ||
      !isWellFormedLobbyRoster(
        incoming, current.lobbyId, current.revision, current.teamIds,
      ) ||
      (incoming.revision === current.revision &&
        !isSameRosterSnapshot(current, incoming)))
    return 'invalid'
  return incoming.participants.some(participant => participant.playerId === actorId)
    ? 'verified' : 'missing'
}

/**
 * A late or malformed response cannot replace a newer local revision.
 * A server response for a different lobby must never reveal or import
 * someone else's roster. Membership is checked before accepting new data.
 */
export function reconcilePolledRoster(
  current: LobbyRoster, incoming: LobbyRoster, actorId: string,
): PolledRosterDecision {
  // Malformed/wrong-room HTTP 200 is not healthy service recovery:
  // report it separately so the caller can apply bounded outage backoff.
  if (!isWellFormedLobbyRoster(
    incoming, current.lobbyId, undefined, current.teamIds,
  )) return 'invalid'
  // Valid but obsolete observations must not revoke even if membership is
  // absent, and do not count as invalid transport data.
  if (incoming.revision < current.revision)
    return 'unchanged'
  if (incoming.revision === current.revision)
    return isSameRosterSnapshot(current, incoming) ? 'unchanged' : 'invalid'
  // Only a structurally valid, *newer* roster can prove membership loss.
  if (!incoming.participants.some(participant => participant.playerId === actorId))
    return 'missing'
  return incoming.revision > current.revision ? 'updated' : 'unchanged'
}

/**
 * Validate the *shared* HTTP DTO contract before any explicit or passive
 * roster is adopted. This does not replace member authorization on .NET.
 * Do not import new/unknown fields that might contain private match data.
 * Hero IDs use the same shared heroes.json catalog as the authoritative
 * .NET OriginalHeroRoster. An unknown hero is not equivalent to no hero.
 * Once the room exists, its definition's team IDs and order are immutable
 * on .NET; even an otherwise well-formed 200 cannot replace them (#119).
 * .NET LobbyState.Restore also requires revision >= participant count:
 * every joined participant consumed at least one revision (#120).
 * A same-version response must match the *entire* previously verified
 * roster, because .NET changes can never rewrite an existing revision (#121).
 */
function isWellFormedLobbyRoster(
  payload: unknown, expectedRoomId?: string, minimumRevision?: number,
  expectedTeamIds?: readonly string[],
): payload is LobbyRoster {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return false
  const data = payload as Record<string, unknown>
  const keys = ['lobbyId', 'revision', 'teamIds', 'participants']
  if (Object.keys(data).length !== keys.length ||
      keys.some(key => !Object.prototype.hasOwnProperty.call(data, key)) ||
      typeof data.lobbyId !== 'string' ||
      !/^room-[0-9a-f]{32}$/.test(data.lobbyId) ||
      (expectedRoomId !== undefined && data.lobbyId !== expectedRoomId) ||
      typeof data.revision !== 'number' ||
      !Number.isSafeInteger(data.revision) || data.revision < 0 ||
      (minimumRevision !== undefined && data.revision < minimumRevision) ||
      !Array.isArray(data.teamIds) ||
      data.teamIds.length < 2 || data.teamIds.length > 4 ||
      !data.teamIds.every((team: unknown) =>
        typeof team === 'string' && /^[a-z0-9-]{1,24}$/.test(team)) ||
      new Set(data.teamIds).size !== data.teamIds.length ||
      (expectedTeamIds !== undefined &&
        (data.teamIds.length !== expectedTeamIds.length ||
          data.teamIds.some((team: unknown, index: number) =>
            team !== expectedTeamIds[index]))) ||
      !Array.isArray(data.participants) ||
      data.participants.length < 1 || data.participants.length > 16 ||
      data.revision < data.participants.length) return false

  const seen = new Set<string>()
  for (const entry of data.participants) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return false
    const player = entry as Record<string, unknown>
    if (Object.keys(player).length !== 3 ||
        !['playerId', 'teamId', 'heroId'].every(key =>
          Object.prototype.hasOwnProperty.call(player, key)) ||
        !isCanonicalGuestPlayerId(player.playerId) ||
        seen.has(player.playerId) ||
        typeof player.teamId !== 'string' ||
        !data.teamIds.includes(player.teamId) ||
        (player.heroId !== null &&
          (typeof player.heroId !== 'string' ||
            getHeroById(player.heroId) === undefined)))
      return false
    seen.add(player.playerId)
  }
  return true
}

/**
 * An explicit response requires valid shape AND the authenticated actor
 * among the participants. A structurally valid passive roster with no actor
 * is treated separately by reconcilePolledRoster as missing membership.
 * For an existing room, also compare its immutable ordered team definition.
 * A same-revision response must also match its full previous snapshot.
 */
export function isVerifiedLobbyRoster(
  payload: unknown, actorId: string, expectedRoomId?: string,
  minimumRevision?: number, expectedTeamIds?: readonly string[],
  previousRoster?: LobbyRoster,
): payload is LobbyRoster {
  return typeof actorId === 'string' && actorId.length > 0 &&
    isWellFormedLobbyRoster(
      payload, expectedRoomId, minimumRevision, expectedTeamIds,
    ) &&
    (previousRoster === undefined ||
      payload.lobbyId !== previousRoster.lobbyId ||
      payload.revision !== previousRoster.revision ||
      isSameRosterSnapshot(previousRoster, payload)) &&
    payload.participants.some(participant => participant.playerId === actorId)
}

/** 401 and 404 are authoritative; transient failures keep the last roster. */
export function classifyRosterPollFailure(
  status: number | undefined,
): PollFailureDecision {
  if (status === 401) return 'expired'
  if (status === 404) return 'missing'
  return 'retain'
}
