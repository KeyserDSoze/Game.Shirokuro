/** Pre-match data only; game positions and planned moves are never exposed. */
export type GuestSession = Readonly<{ playerId: string; kind: 'guest' }>
/** Match the 128-bit guest ID format from GuestSessions.NewPlayerId() on .NET. */
export function isCanonicalGuestPlayerId(value: unknown): value is string {
  return typeof value === 'string' && /^guest-[0-9a-f]{32}$/.test(value)
}

/** A signed non-guest identity cannot be adopted as a guest session. */
export type CurrentSession = GuestSession | Readonly<{ playerId: string; kind: 'other' }>

/**
 * HTTP 200 is not proof of a guest identity: the server issues 128-bit,
 * lowercase-hex guest IDs. Malformed JSON must not become a new actor or
 * be mistaken for a confirmed HTTP 401 session revocation.
 */
export function parseCurrentSession(payload: unknown): CurrentSession {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload))
    throw new Error('Invalid session envelope')
  const data = payload as Record<string, unknown>
  if (Object.keys(data).length !== 2 ||
      !Object.prototype.hasOwnProperty.call(data, 'kind') ||
      !Object.prototype.hasOwnProperty.call(data, 'playerId') ||
      typeof data.playerId !== 'string' || data.playerId.length === 0)
    throw new Error('Invalid session envelope')

  if (data.kind === 'guest' && isCanonicalGuestPlayerId(data.playerId))
    return { kind: 'guest', playerId: data.playerId }
  // A well-formed non-guest session still clears an obsolete guest in the
  // existing recheck logic, but it must never masquerade as a guest.
  if (data.kind === 'other' && data.playerId.trim() === data.playerId)
    return { kind: 'other', playerId: data.playerId }
  throw new Error('Invalid session envelope')
}

export function parseGuestSession(payload: unknown): GuestSession {
  const session = parseCurrentSession(payload)
  if (session.kind !== 'guest') throw new Error('Not a guest session')
  return session
}
export type LobbyParticipant = Readonly<{
  playerId: string
  teamId: string
  heroId: string | null
}>
export type LobbyRoster = Readonly<{
  lobbyId: string
  revision: number
  teamIds: readonly string[]
  participants: readonly LobbyParticipant[]
}>

export class LobbyHttpError extends Error {
  constructor(readonly status: number) {
    super('Lobby HTTP error: ' + status)
    this.name = 'LobbyHttpError'
  }
}

/** A local input error must never be mistaken for a failed network fetch. */
export class LobbyInputError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'LobbyInputError'
  }
}

/** Technical validation only. This is not a rule about in-game team sizes. */
export function parseTeamIds(input: string): string[] {
  if (input.length > 100) throw new LobbyInputError('Invalid teams')
  const teams = input.split(',').map(value => value.trim())
  if (teams.length < 2 || teams.length > 4 ||
      teams.some(team => !/^[a-z0-9-]{1,24}$/.test(team)) ||
      new Set(teams).size !== teams.length) {
    throw new LobbyInputError('Invalid teams')
  }
  return teams
}

/** Reject malformed or injected invite references before issuing a request. */
export function parseInviteCode(input: string): string {
  const code = input.trim()
  if (!/^room-[0-9a-f]{32}$/.test(code)) throw new LobbyInputError('Invalid invitation')
  return code
}

/** Bounded browser wait; a timed-out mutation may still commit server-side. */
export const LOBBY_HTTP_REQUEST_TIMEOUT_MS = 15_000
/** Passive roster reads must finish before the next normal 15s poll window. */
export const LOBBY_PASSIVE_PEEK_TIMEOUT_MS = 12_000

async function request<T>(path: string, method = 'GET', body?: object): Promise<T> {
  const mutating = method !== 'GET'
  const controller = new AbortController()
  let timeoutId: ReturnType<typeof setTimeout> | undefined
  // Race the whole operation, including JSON decoding: an abort-ignoring
  // transport must not keep all explicit lobby controls busy indefinitely.
  const timeout = new Promise<never>((_resolve, reject) => {
    timeoutId = setTimeout(() => {
      reject(new Error('Lobby request timed out'))
      controller.abort()
    }, LOBBY_HTTP_REQUEST_TIMEOUT_MS)
  })
  const operation = (async (): Promise<T> => {
    const response = await fetch('/api' + path, {
      method,
      credentials: 'include',
      cache: 'no-store',
      signal: controller.signal,
      headers: {
        ...(mutating ? { 'X-Shirokuro-Request': '1' } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      },
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    })
    if (!response.ok) throw new LobbyHttpError(response.status)
    if (response.status === 204) return undefined as T
    return response.json() as Promise<T>
  })()
  try {
    return await Promise.race([operation, timeout])
  } finally {
    clearTimeout(timeoutId)
  }
}

export const guestLobbyApi = {
  // Decode at the boundary; a forged/corrupted 200 never replaces identity.
  session: async (): Promise<CurrentSession> =>
    parseCurrentSession(await request<unknown>('/sessions/me')),
  signIn: async (): Promise<GuestSession> =>
    parseGuestSession(await request<unknown>('/sessions/guest', 'POST')),
  signOut: () => request<void>('/sessions/guest', 'DELETE'),
  create: (teamIds: readonly string[], teamId: string) =>
    request<LobbyRoster>('/lobbies', 'POST', { teamIds, teamId }),
  join: (code: string, teamId: string) =>
    request<LobbyRoster>('/lobbies/' + parseInviteCode(code) + '/join', 'POST', { teamId }),
  read: (code: string) =>
    request<LobbyRoster>('/lobbies/' + parseInviteCode(code) + '/roster'),
  // Only a 304 with matching strong ETag and no-store is a successful,
  // *authorized* no-change result; every other 304 is an outage. The
  // validator contains only the last verified room revision,
  // never an invite, player ID, cookie or hidden match information.
  peek: async (code: string, knownRevision?: number, signal?: AbortSignal): Promise<LobbyRoster | null> => {
    const room = parseInviteCode(code)
    const tag: Record<string, string> = knownRevision !== undefined &&
      Number.isSafeInteger(knownRevision) && knownRevision >= 0
      ? { 'If-None-Match': '"rev-' + knownRevision + '"' } : {}
    // The effect can cancel a stale poll immediately. A separate internal
    // controller also cancels a read whose fetch or JSON body never completes.
    // Neither signal nor timeout is evidence of guest revocation or room loss.
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    const controller = new AbortController()
    let cancelRead!: () => void
    const cancelled = new Promise<never>((_resolve, reject) => {
      cancelRead = () => {
        controller.abort()
        reject(new DOMException('Aborted', 'AbortError'))
      }
    })
    signal?.addEventListener('abort', cancelRead, { once: true })
    let timeoutId: ReturnType<typeof setTimeout> | undefined
    const deadline = new Promise<never>((_resolve, reject) => {
      timeoutId = setTimeout(() => {
        reject(new Error('Passive lobby roster read timed out'))
        controller.abort()
      }, LOBBY_PASSIVE_PEEK_TIMEOUT_MS)
    })
    const operation = (async (): Promise<LobbyRoster | null> => {
      const response = await fetch('/api/lobbies/' + room + '/roster/peek', {
        method: 'GET',
        credentials: 'include',
        cache: 'no-store',
        headers: tag,
        signal: controller.signal,
      })
      if (response.status === 304) {
        // Only the member-authorized server 304 for the *exact revision
        // requested* can verify a cached roster. A broken edge/proxy may
        // emit a naked, weak, wrong-revision or cacheable 304; never treat
        // that as session/membership authority or healthy Redis recovery.
        // The production .NET roster endpoint sets both headers after its
        // auth/guest-authority checks. An invalid 304 is a transient
        // protocol failure (stale + bounded backoff), never a 401/404.
        const expected = tag['If-None-Match']
        const cacheControl = response.headers?.get('Cache-Control')
        const noStore = cacheControl?.split(',').some(value =>
          value.trim().toLowerCase() === 'no-store') ?? false
        if (!expected || response.headers?.get('ETag') !== expected || !noStore)
          throw new Error('Unverified roster 304')
        return null
      }
      if (!response.ok) throw new LobbyHttpError(response.status)
      return response.json() as Promise<LobbyRoster>
    })()
    try {
      // An abort-ignoring transport must never stall the poll forever.
      return await Promise.race([operation, deadline, cancelled])
    } finally {
      clearTimeout(timeoutId)
      signal?.removeEventListener('abort', cancelRead)
    }
  },
  chooseHero: (code: string, heroId: string, expectedRevision: number) =>
    request<LobbyRoster>('/lobbies/' + parseInviteCode(code) + '/hero',
      'PATCH', { heroId, expectedRevision }),
  changeTeam: (code: string, teamId: string, expectedRevision: number) =>
    request<LobbyRoster>('/lobbies/' + parseInviteCode(code) + '/team',
      'PATCH', { teamId, expectedRevision }),
  leave: (code: string, expectedRevision: number) =>
    request<void>('/lobbies/' + parseInviteCode(code) + '/membership',
      'DELETE', { expectedRevision }),
}

