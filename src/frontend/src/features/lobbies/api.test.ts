import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  guestLobbyApi, LobbyHttpError, parseInviteCode, parseTeamIds,
} from './api'

const roomCode = 'room-' + 'a'.repeat(32)

afterEach(() => vi.unstubAllGlobals())

describe('guest lobby client boundaries', () => {
  it.each([
    ['white,black', ['white', 'black']],
    ['white, black, gold', ['white', 'black', 'gold']],
    ['alpha,beta,gamma,delta', ['alpha', 'beta', 'gamma', 'delta']],
  ])('parses technical configuration %s', (input, expected) => {
    expect(parseTeamIds(input)).toEqual(expected)
  })

  it.each([
    '', 'white', 'white,white', 'white,White',
    'white,bad space', 'white,black,gold,red,blue',
    'white,' + 'a'.repeat(25), 'a'.repeat(101),
  ])('rejects invalid teams %s', input => {
    expect(() => parseTeamIds(input)).toThrow()
  })

  it('restricts invite ID to 128-bit random-looking room identifiers', () => {
    expect(parseInviteCode(' ' + roomCode + '\n')).toBe(roomCode)
    for (const input of ['', 'room-123', 'room-' + 'A'.repeat(32),
      '../api/sessions/me', roomCode + '/roster', roomCode + '?playerId=alice']) {
      expect(() => parseInviteCode(input)).toThrow()
    }
  })

  it('never supplies a playerId when creating and joining the room', async () => {
    const mocked = vi.fn().mockResolvedValue({
      ok: true, status: 200, json: async () => ({
        lobbyId: roomCode, revision: 1, teamIds: ['white', 'black'],
        participants: [],
      }),
    })
    vi.stubGlobal('fetch', mocked)
    await guestLobbyApi.create(['white', 'black'], 'white')
    await guestLobbyApi.join(roomCode, 'black')
    expect(mocked).toHaveBeenCalledTimes(2)
    const [createdUrl, createdOptions] = mocked.mock.calls[0] as [string, RequestInit]
    expect(createdUrl).toBe('/api/lobbies')
    expect(createdOptions.credentials).toBe('include')
    expect(createdOptions.method).toBe('POST')
    expect(createdOptions.headers).toEqual({
      'X-Shirokuro-Request': '1', 'Content-Type': 'application/json',
    })
    expect(JSON.parse(createdOptions.body as string)).toEqual({
      teamIds: ['white', 'black'], teamId: 'white',
    })
    const [joinUrl, joinOptions] = mocked.mock.calls[1] as [string, RequestInit]
    expect(joinUrl).toBe('/api/lobbies/' + roomCode + '/join')
    expect(JSON.parse(joinOptions.body as string)).toEqual({ teamId: 'black' })
  })

  it('uses server revision for self-only changes and sends no secret match state', async () => {
    const mocked = vi.fn().mockResolvedValue({
      ok: true, status: 200, json: async () => ({ revision: 5 }),
    })
    vi.stubGlobal('fetch', mocked)
    await guestLobbyApi.chooseHero(roomCode, 'kira', 4)
    await guestLobbyApi.changeTeam(roomCode, 'black', 5)
    const req0 = mocked.mock.calls[0][1] as RequestInit
    const req1 = mocked.mock.calls[1][1] as RequestInit
    expect(JSON.parse(req0.body as string)).toEqual({ heroId: 'kira', expectedRevision: 4 })
    expect(JSON.parse(req1.body as string)).toEqual({ teamId: 'black', expectedRevision: 5 })
    expect(JSON.stringify([req0, req1])).not.toMatch(/plannedDestination|position|matchState/i)
  })

  it('preserves 409 conflicts so UI can require a refresh', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 409 }))
    await expect(guestLobbyApi.chooseHero(roomCode, 'kira', 1))
      .rejects.toMatchObject({ name: 'LobbyHttpError', status: 409 })
    expect(new LobbyHttpError(404).status).toBe(404)
  })

  it('sign-in performs no magic persistence or browser-side identity generation', async () => {
    const mocked = vi.fn().mockResolvedValue({
      ok: true, status: 200,
      json: async () => ({ playerId: 'guest-from-server', kind: 'guest' }),
    })
    vi.stubGlobal('fetch', mocked)
    const session = await guestLobbyApi.signIn()
    expect(session.playerId).toBe('guest-from-server')
    expect(mocked.mock.calls[0][0]).toBe('/api/sessions/guest')
    expect((mocked.mock.calls[0][1] as RequestInit).body).toBeUndefined()
  })
})

