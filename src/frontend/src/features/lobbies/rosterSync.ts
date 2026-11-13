import type { LobbyRoster } from './api'

/** Read-only, member-scoped, pre-match synchronization; not match state. */
export const LOBBY_ROSTER_POLL_INTERVAL_MS = 15_000
export const LOBBY_ROSTER_MAX_BACKOFF_MS = 120_000

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

export type PolledRosterDecision = 'updated' | 'unchanged' | 'missing'
export type PollFailureDecision = 'expired' | 'missing' | 'retain'

/**
 * A late or malformed response cannot replace a newer local revision.
 * A server response for a different lobby must never reveal or import
 * someone else's roster. Membership is checked before accepting new data.
 */
export function reconcilePolledRoster(
  current: LobbyRoster, incoming: LobbyRoster, actorId: string,
): PolledRosterDecision {
  if (!incoming || incoming.lobbyId !== current.lobbyId ||
      !Number.isSafeInteger(incoming.revision) || incoming.revision < current.revision ||
      !Array.isArray(incoming.teamIds) ||
      !incoming.teamIds.every(team => typeof team === 'string') ||
      !Array.isArray(incoming.participants) ||
      !incoming.participants.every(participant =>
        participant != null && typeof participant.playerId === 'string' &&
        typeof participant.teamId === 'string' &&
        (participant.heroId === null || typeof participant.heroId === 'string'))) {
    return 'unchanged'
  }
  if (!incoming.participants.some(participant => participant.playerId === actorId)) {
    return 'missing'
  }
  return incoming.revision > current.revision ? 'updated' : 'unchanged'
}

/**
 * Validate explicit pre-match responses before putting them into the UI.
 * Server authentication remains authoritative; a 200 alone is not permission
 * to import an unrelated, stale or malformed private roster.
 */
export function isVerifiedLobbyRoster(
  payload: unknown, actorId: string, expectedRoomId?: string,
  minimumRevision?: number,
): payload is LobbyRoster {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload) ||
      typeof actorId !== 'string' || actorId.length === 0) return false
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
      !Array.isArray(data.participants) ||
      data.participants.length < 1 || data.participants.length > 16) return false

  const seen = new Set<string>()
  for (const entry of data.participants) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return false
    const player = entry as Record<string, unknown>
    if (Object.keys(player).length !== 3 ||
        !['playerId', 'teamId', 'heroId'].every(key =>
          Object.prototype.hasOwnProperty.call(player, key)) ||
        typeof player.playerId !== 'string' || player.playerId.length === 0 ||
        seen.has(player.playerId) ||
        typeof player.teamId !== 'string' ||
        !data.teamIds.includes(player.teamId) ||
        (player.heroId !== null &&
          (typeof player.heroId !== 'string' || player.heroId.length === 0)))
      return false
    seen.add(player.playerId)
  }
  return seen.has(actorId)
}

/** 401 and 404 are authoritative; transient failures keep the last roster. */
export function classifyRosterPollFailure(
  status: number | undefined,
): PollFailureDecision {
  if (status === 401) return 'expired'
  if (status === 404) return 'missing'
  return 'retain'
}
