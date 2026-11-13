import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  guestLobbyApi, LobbyHttpError, LobbyInputError, parseInviteCode, parseTeamIds,
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

  it('classifies local input errors separately from HTTP and network failures', () => {
    expect(() => parseTeamIds('white,white')).toThrow(LobbyInputError)
    expect(() => parseInviteCode('../../admin')).toThrow(LobbyInputError)
    expect(() => parseTeamIds('white,black')).not.toThrow()
    expect(new LobbyHttpError(503)).not.toBeInstanceOf(LobbyInputError)
    expect(new TypeError('Failed to fetch')).not.toBeInstanceOf(LobbyInputError)
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

  it.each([401, 403, 429, 503])('propagates HTTP %i without inventing a new identity', async status => {
    const mocked = vi.fn().mockResolvedValue({ ok: false, status })
    vi.stubGlobal('fetch', mocked)
    await expect(guestLobbyApi.read(roomCode)).rejects.toMatchObject({
      name: 'LobbyHttpError', status,
    })
    expect(mocked).toHaveBeenCalledOnce()
    expect((mocked.mock.calls[0][1] as RequestInit).method).toBe('GET')
  })

  it('sends passive roster polls only to the read-only, no-heartbeat route', async () => {
    const mocked = vi.fn().mockResolvedValue({
      ok: true, status: 200, json: async () => ({
        lobbyId: roomCode, revision: 2, teamIds: ['white', 'black'],
        participants: [],
      }),
    })
    vi.stubGlobal('fetch', mocked)
    await guestLobbyApi.peek(roomCode, 2)
    await guestLobbyApi.read(roomCode)

    expect(mocked.mock.calls[0][0])
      .toBe('/api/lobbies/' + roomCode + '/roster/peek')
    expect(mocked.mock.calls[1][0])
      .toBe('/api/lobbies/' + roomCode + '/roster')
    for (const [, request] of mocked.mock.calls as [string, RequestInit][]) {
      expect(request.method).toBe('GET')
      expect(request.credentials).toBe('include')
      expect(request.cache).toBe('no-store')
      expect(request.body).toBeUndefined()
      expect(request.headers).toEqual(
        request === mocked.mock.calls[0][1]
          ? { 'If-None-Match': '"rev-2"' } : {})
    }
    await expect(guestLobbyApi.peek('../bad', 2)).rejects.toThrow(LobbyInputError)
    expect(mocked).toHaveBeenCalledTimes(2)
  })

  it('handles 304 with no JSON parsing and preserves an already verified roster', async () => {
    const json = vi.fn(() => { throw new Error('A 304 must not parse private JSON') })
    const mocked = vi.fn().mockResolvedValue({ ok: false, status: 304, json })
    vi.stubGlobal('fetch', mocked)
    expect(await guestLobbyApi.peek(roomCode, 7)).toBeNull()
    expect(json).not.toHaveBeenCalled()
    expect(mocked).toHaveBeenCalledWith(
      '/api/lobbies/' + roomCode + '/roster/peek',
      expect.objectContaining({
        method: 'GET',
        cache: 'no-store',
        credentials: 'include',
        headers: { 'If-None-Match': '"rev-7"' },
      }),
    )
  })

  it.each([undefined, -1, 1.25, Number.NaN, Infinity, -Infinity])(
    'ignores invalid/unavailable revision validators (%s) without losing full 200', async revision => {
      const roster = { lobbyId: roomCode, revision: 4,
        teamIds: ['white', 'black'], participants: [] }
      const mocked = vi.fn().mockResolvedValue({
        ok: true, status: 200, json: async () => roster,
      })
      vi.stubGlobal('fetch', mocked)
      expect(await guestLobbyApi.peek(roomCode, revision)).toEqual(roster)
      expect((mocked.mock.calls[0][1] as RequestInit).headers).toEqual({})
    },
  )

  it.each([401, 403, 404, 429, 503])(
    'does not turn a genuine passive poll %i failure into a false 304', async status => {
      const mocked = vi.fn().mockResolvedValue({ ok: false, status })
      vi.stubGlobal('fetch', mocked)
      await expect(guestLobbyApi.peek(roomCode, 5))
        .rejects.toMatchObject({ name: 'LobbyHttpError', status })
    },
  )

  it('forwards AbortSignal only to passive roster polling and never to an explicit refresh', async () => {
    const controller = new AbortController()
    const roster = {
      lobbyId: roomCode, revision: 2, teamIds: ['white', 'black'], participants: [],
    }
    const mocked = vi.fn((_path: string, options: RequestInit) => {
      if (options.signal === controller.signal) {
        return new Promise((_resolve, reject) => {
          options.signal?.addEventListener('abort', () =>
            reject(new DOMException('Aborted', 'AbortError')), { once: true })
        })
      }
      return Promise.resolve({ ok: true, status: 200, json: async () => roster })
    })
    vi.stubGlobal('fetch', mocked)
    const pending = guestLobbyApi.peek(roomCode, 2, controller.signal)
    expect(mocked).toHaveBeenCalledWith(
      '/api/lobbies/' + roomCode + '/roster/peek',
      expect.objectContaining({
        method: 'GET', signal: controller.signal,
        headers: { 'If-None-Match': '"rev-2"' },
      }),
    )
    controller.abort()
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' })
    expect(await guestLobbyApi.read(roomCode)).toEqual(roster)
    expect((mocked.mock.calls[1][1] as RequestInit).signal).toBeUndefined()
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

