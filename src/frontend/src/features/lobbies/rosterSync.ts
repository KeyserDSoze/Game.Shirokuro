import type { LobbyRoster } from './api'

/** Read-only, member-scoped, pre-match synchronization; not match state. */
export const LOBBY_ROSTER_POLL_INTERVAL_MS = 15_000

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
