/** Pre-match data only; game positions and planned moves are never exposed. */
export type GuestSession = Readonly<{ playerId: string; kind: 'guest' }>
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

async function request<T>(path: string, method = 'GET', body?: object): Promise<T> {
  const mutating = method !== 'GET'
  const response = await fetch('/api' + path, {
    method,
    credentials: 'include',
    cache: 'no-store',
    headers: {
      ...(mutating ? { 'X-Shirokuro-Request': '1' } : {}),
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
  })
  if (!response.ok) throw new LobbyHttpError(response.status)
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export const guestLobbyApi = {
  session: () => request<GuestSession>('/sessions/me'),
  signIn: () => request<GuestSession>('/sessions/guest', 'POST'),
  signOut: () => request<void>('/sessions/guest', 'DELETE'),
  create: (teamIds: readonly string[], teamId: string) =>
    request<LobbyRoster>('/lobbies', 'POST', { teamIds, teamId }),
  join: (code: string, teamId: string) =>
    request<LobbyRoster>('/lobbies/' + parseInviteCode(code) + '/join', 'POST', { teamId }),
  read: (code: string) =>
    request<LobbyRoster>('/lobbies/' + parseInviteCode(code) + '/roster'),
  // 304 is a successful *authorized* no-change result, not a network
  // failure. The validator contains only the last verified room revision,
  // never an invite, player ID, cookie or hidden match information.
  peek: async (code: string, knownRevision?: number): Promise<LobbyRoster | null> => {
    const room = parseInviteCode(code)
    const tag = knownRevision !== undefined &&
      Number.isSafeInteger(knownRevision) && knownRevision >= 0
      ? { 'If-None-Match': '"rev-' + knownRevision + '"' } : {}
    const response = await fetch('/api/lobbies/' + room + '/roster/peek', {
      method: 'GET',
      credentials: 'include',
      cache: 'no-store',
      headers: tag,
    })
    if (response.status === 304) return null
    if (!response.ok) throw new LobbyHttpError(response.status)
    return response.json() as Promise<LobbyRoster>
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

