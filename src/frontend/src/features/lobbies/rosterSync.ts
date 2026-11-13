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

/** 401 and 404 are authoritative; transient failures keep the last roster. */
export function classifyRosterPollFailure(
  status: number | undefined,
): PollFailureDecision {
  if (status === 401) return 'expired'
  if (status === 404) return 'missing'
  return 'retain'
}
