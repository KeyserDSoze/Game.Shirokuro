import { expect, test } from '@playwright/test'

test('returning online after an outage advances only one verified roster read without mutation bursts', async ({ page }) => {
  test.setTimeout(65_000)
  const code = 'room-' + 'e'.repeat(32)
  const playerId = 'guest-' + 'e'.repeat(32)
  const original = {
    lobbyId: code, revision: 1, teamIds: ['white', 'black'],
    participants: [{ playerId, teamId: 'white', heroId: null }],
  }
  const newer = { ...original, revision: 2,
    participants: [{ playerId, teamId: 'black', heroId: null }] }
  let signedIn = false
  let responseStatus = 503
  let reads = 0
  const times: number[] = []
  const validators: (string | undefined)[] = []
  const writes: string[] = []

  // Transport-only guest fixture; the separate real-browser scenario covers
  // cookie issuance and two independent players. This test never consumes
  // the shared CI IP quota or creates a real lobby.
  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { kind: 'guest', playerId } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    signedIn = true
    await route.fulfill({ status: 200, json: { kind: 'guest', playerId } })
  })
  await page.route('**/api/lobbies', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    await route.fulfill({ status: 201, json: original })
  })
  await page.route('**/api/lobbies/' + code + '/roster', route =>
    route.fulfill({ status: 200, json: newer }))
  await page.route('**/api/lobbies/' + code + '/roster/peek', async route => {
    if (route.request().method() !== 'GET') return route.continue()
    reads++
    times.push(Date.now())
    validators.push(route.request().headers()['if-none-match'])
    await route.fulfill(responseStatus === 503
      ? { status: 503, headers: { 'Cache-Control': 'no-store' }, body: '{}' }
      : { status: 200, headers: { 'Cache-Control': 'no-store' }, json: newer })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  page.on('request', request => {
    if (['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method()) &&
        new URL(request.url()).pathname.startsWith('/api/')) {
      writes.push(request.url())
    }
  })

  // Normal first passive poll fails on 503, retaining the verified room.
  await expect.poll(() => reads, { timeout: 25_000 }).toBe(1)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await expect(page.getByTestId('lobby-roster-stale')).toContainText('ultima verifica')
  await expect(page.getByTestId('lobby-roster-stale')).toHaveAttribute('role', 'status')
  await expect(page.getByTestId('lobby-revision')).toContainText('1')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await page.waitForTimeout(200) // Ensure its async finally has scheduled backoff.

  // Simulate the browser becoming visible after an API failover. Even 50
  // duplicate online/focus/visibility hints must share a single deadline.
  responseStatus = 200
  await page.evaluate(() => {
    for (let i = 0; i < 50; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
      document.dispatchEvent(new Event('visibilitychange'))
    }
  })
  // The recovering read cannot start immediately, even after all 150 hints.
  await page.waitForTimeout(300)
  expect(reads).toBe(1)
  await expect.poll(() => reads, { timeout: 24_000 }).toBe(2)
  expect(times[1] - times[0]).toBeGreaterThanOrEqual(14_500)
  expect(times[1] - times[0]).toBeLessThan(27_000)
  expect(validators).toEqual(['"rev-1"', '"rev-1"'])
  await expect(page.getByTestId('lobby-revision')).toContainText('2')
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  await expect(page.getByTestId('lobby-roster')).toContainText('black')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  // No guest issuance, mutation replay, or identity/room change after wake.
  expect(writes).toEqual([])

  await page.evaluate(() => {
    for (let i = 0; i < 50; i++) window.dispatchEvent(new Event('online'))
  })
  await page.waitForTimeout(250)
  expect(reads).toBe(2)
  expect(writes).toEqual([])

  // Offline is evidence that the snapshot can no longer be confirmed,
  // but it is not a reason to discard a previously verified guest/room.
  await page.evaluate(() => window.dispatchEvent(new Event('offline')))
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  // A deliberate read-only, authenticated refresh can confirm the current
  // revision without waiting for the next passive poll or issuing a write.
  await page.getByTestId('lobby-refresh').click()
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  await expect(page.getByTestId('lobby-revision')).toContainText('2')
  expect(writes).toEqual([])
})


test('a persistent roster outage spends only one wake and preserves bounded backoff', async ({ page }) => {
  test.setTimeout(60_000)
  // Clock control exercises 30/60s backoff without sleeping for minutes.
  await page.clock.install()
  const code = 'room-' + 'f'.repeat(32)
  const actor = 'guest-' + 'f'.repeat(32)
  const original = {
    lobbyId: code, revision: 1, teamIds: ['white', 'black'],
    participants: [{ playerId: actor, teamId: 'white', heroId: null }],
  }
  let signedIn = false
  let reads = 0
  let mutations = 0
  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { playerId: actor, kind: 'guest' } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    mutations++
    signedIn = true
    return route.fulfill({ status: 200, json: { playerId: actor, kind: 'guest' } })
  })
  await page.route('**/api/lobbies', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    mutations++
    return route.fulfill({ status: 201, json: original })
  })
  await page.route('**/api/lobbies/' + code + '/roster/peek', route => {
    reads++
    return route.fulfill({
      status: 503, headers: { 'Cache-Control': 'no-store' },
      json: { status: 'unavailable' },
    })
  })
  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  expect(mutations).toBe(2)

  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(1)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()

  // The first wake moves the pending 30s recovery retry forward, but
  // cannot violate the 15s minimum since the previous attempted GET.
  await page.evaluate(() => {
    for (let i = 0; i < 40; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(2)

  // A second set of hints after its 30s cooldown MUST NOT reduce the
  // naturally scheduled 60s retry following the second 503.
  await page.clock.fastForward(30_100)
  await page.evaluate(() => {
    for (let i = 0; i < 40; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(16_000)
  expect(reads).toBe(2)
  // Failure #2 uses positive 0..20% jitter: a 60s base can run as
  // late as 72s. Observe after the bounded upper limit, not at 61s.
  await page.clock.fastForward(27_000)
  await expect.poll(() => reads).toBe(3)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await expect(page.getByTestId('lobby-player')).toContainText(actor)
  expect(mutations).toBe(2) // never replay POST/PATCH/DELETE due to a hint
})


test('an old-revision 200 after Redis outage preserves backoff and cannot rearm wake hints', async ({ page }) => {
  test.setTimeout(65_000)
  // Simulate minutes of replica lag without long wall-clock Playwright sleeps.
  await page.clock.install()
  const code = 'room-' + 'c'.repeat(32)
  const actor = 'guest-' + 'c'.repeat(32)
  const original = {
    lobbyId: code, revision: 4, teamIds: ['white', 'black'],
    participants: [{ playerId: actor, teamId: 'white', heroId: null }],
  }
  const older = { ...original, revision: 3 }
  const newer = { ...original, revision: 5,
    participants: [{ playerId: actor, teamId: 'black', heroId: null }] }
  let signedIn = false
  let reads = 0
  let mutations = 0
  const validators: (string | undefined)[] = []

  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { kind: 'guest', playerId: actor } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', route => {
    if (route.request().method() !== 'POST') return route.continue()
    signedIn = true
    mutations++
    return route.fulfill({ status: 200, json: { kind: 'guest', playerId: actor } })
  })
  await page.route('**/api/lobbies', route => {
    if (route.request().method() !== 'POST') return route.continue()
    mutations++
    return route.fulfill({ status: 201, json: original })
  })
  await page.route('**/api/lobbies/' + code + '/roster/peek', route => {
    reads++
    validators.push(route.request().headers()['if-none-match'])
    if (reads <= 2 || reads === 5) return route.fulfill({
      status: 503, headers: { 'Cache-Control': 'no-store' },
      json: { status: 'unavailable' },
    })
    if (reads === 3) return route.fulfill({
      status: 200, headers: { 'Cache-Control': 'no-store' }, json: older,
    })
    if (reads === 4) return route.fulfill({
      status: 200, headers: { 'Cache-Control': 'no-store' }, json: newer,
    })
    // A verified authorized 304 clears the second outage episode.
    return route.fulfill({
      status: 304,
      headers: { 'Cache-Control': 'no-store', ETag: '"rev-5"' },
    })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-revision')).toContainText('4')
  expect(mutations).toBe(2)

  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(1)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()

  // Consume exactly one early wake while the API keeps returning 503.
  await page.evaluate(() => {
    for (let i = 0; i < 40; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(2)
  // The handler increments reads before its fetch response finishes. Give
  // its finally a browser event-loop turn to install the next timer, then
  // cross the *upper* 72s jittered retry bound (not the 60s minimum).
  await page.clock.runFor(200)
  await page.clock.fastForward(75_000)
  await expect.poll(() => reads).toBe(3)
  await page.clock.runFor(200)

  // This third GET returned HTTP 200, but revision 3 is older than our
  // locally verified 4. Do not adopt its payload or claim recovery.
  await expect(page.getByTestId('lobby-revision')).toContainText('4')
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  expect(validators).toEqual(['"rev-4"', '"rev-4"', '"rev-4"'])

  // Old, valid 200 is a failure of freshness. 40 fresh hints after the
  // hint cooldown must not replace its next 120s automatic retry.
  await page.clock.fastForward(30_100)
  await page.evaluate(() => {
    for (let i = 0; i < 40; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(16_000)
  expect(reads).toBe(3)
  // Failure #3 is capped at 120s regardless of jitter. All three
  // windows together are >120s, but the 46s intermediate assertion
  // above ensures repeated wake hints did not shorten the deadline.
  await page.clock.fastForward(74_000)
  await expect.poll(() => reads).toBe(4)
  await expect(page.getByTestId('lobby-revision')).toContainText('5')
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  await expect(page.getByTestId('lobby-roster')).toContainText('black')

  // A fully verified revision 5 re-arms exactly one wake for the next
  // outage. The next 503 stays stale; a subsequent 304 recovers it.
  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(5)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await page.evaluate(() => {
    for (let i = 0; i < 40; i++) window.dispatchEvent(new Event('online'))
  })
  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(6)
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  await expect(page.getByTestId('lobby-revision')).toContainText('5')
  await expect(page.getByTestId('lobby-player')).toContainText(actor)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  expect(validators).toEqual([
    '"rev-4"', '"rev-4"', '"rev-4"', '"rev-4"', '"rev-5"', '"rev-5"',
  ])
  expect(mutations).toBe(2) // no POST/PATCH/DELETE retry during polling
})


test('mismatched 304 never confirms roster freshness or bypasses bounded outage backoff', async ({ page }) => {
  test.setTimeout(65_000)
  await page.clock.install()
  const roomId = 'room-' + 'd'.repeat(32)
  const actor = 'guest-' + 'd'.repeat(32)
  const snapshot = {
    lobbyId: roomId, revision: 4, teamIds: ['white', 'black'],
    participants: [{ playerId: actor, teamId: 'white', heroId: null }],
  }
  let signedIn = false
  let reads = 0
  let writes = 0
  const validators: (string | undefined)[] = []
  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { kind: 'guest', playerId: actor } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', route => {
    if (route.request().method() !== 'POST') return route.continue()
    signedIn = true
    writes++
    return route.fulfill({ status: 200, json: { kind: 'guest', playerId: actor } })
  })
  await page.route('**/api/lobbies', route => {
    if (route.request().method() !== 'POST') return route.continue()
    writes++
    return route.fulfill({ status: 201, json: snapshot })
  })
  await page.route('**/api/lobbies/' + roomId + '/roster/peek', route => {
    reads++
    validators.push(route.request().headers()['if-none-match'])
    if (reads === 1) return route.fulfill({
      status: 503, headers: { 'Cache-Control': 'no-store' },
      json: { status: 'unavailable' },
    })
    if (reads === 2) return route.fulfill({
      status: 304, headers: {
        ETag: '"rev-3"', 'Cache-Control': 'no-store',
      },
    })
    return route.fulfill({
      status: 304, headers: {
        ETag: '"rev-4"', 'Cache-Control': 'no-store',
      },
    })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomId)
  expect(writes).toBe(2)

  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(1)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await page.clock.runFor(200)

  // Spend the one early wake on a wrong-revision 304. A 304 alone, even
  // with valid Cache-Control, must not make the stale room current.
  await page.evaluate(() => {
    for (let i = 0; i < 30; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(2)
  await page.clock.runFor(200)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await expect(page.getByTestId('lobby-revision')).toContainText('4')

  // Invalid 304 increments the second-failure backoff to 60..72s.
  // Subsequent focus bursts cannot bring it forward again.
  await page.clock.fastForward(35_000)
  await page.evaluate(() => {
    for (let i = 0; i < 30; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(15_000)
  expect(reads).toBe(2)
  await page.clock.fastForward(25_000)
  await expect.poll(() => reads).toBe(3)
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  await expect(page.getByTestId('lobby-revision')).toContainText('4')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomId)
  await expect(page.getByTestId('lobby-player')).toContainText(actor)
  expect(validators).toEqual(['"rev-4"', '"rev-4"', '"rev-4"'])
  expect(writes).toBe(2)
})

test('contradictory no-store 304 preserves stale roster and 60s retry budget', async ({ page }) => {
  test.setTimeout(65_000)
  await page.clock.install()
  const code = 'room-' + 'b'.repeat(32)
  const actor = 'guest-' + 'b'.repeat(32)
  const snapshot = {
    lobbyId: code, revision: 4, teamIds: ['white', 'black'],
    participants: [{ playerId: actor, teamId: 'white', heroId: null }],
  }
  let signedIn = false
  let reads = 0
  let writes = 0
  const validators: (string | undefined)[] = []

  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { kind: 'guest', playerId: actor } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', route => {
    if (route.request().method() !== 'POST') return route.continue()
    signedIn = true
    writes++
    return route.fulfill({ status: 200, json: { kind: 'guest', playerId: actor } })
  })
  await page.route('**/api/lobbies', route => {
    if (route.request().method() !== 'POST') return route.continue()
    writes++
    return route.fulfill({ status: 201, json: snapshot })
  })
  await page.route('**/api/lobbies/' + code + '/roster/peek', route => {
    reads++
    validators.push(route.request().headers()['if-none-match'])
    if (reads === 1) return route.fulfill({
      status: 503, headers: { 'Cache-Control': 'no-store' },
      json: { status: 'unavailable' },
    })
    if (reads === 2) return route.fulfill({
      status: 304, headers: {
        ETag: '"rev-4"', 'Cache-Control': 'no-store, public, max-age=60',
      },
    })
    return route.fulfill({
      status: 304, headers: {
        ETag: '"rev-4"', 'Cache-Control': 'private, no-store',
      },
    })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  expect(writes).toBe(2)

  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(1)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await page.clock.runFor(200)

  // A single wake may advance the retry, not turn a cacheable 304 into a
  // fresh roster. Even a matching strong ETag is insufficient.
  await page.evaluate(() => {
    for (let i = 0; i < 30; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(15_100)
  await expect.poll(() => reads).toBe(2)
  await page.clock.runFor(200)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await expect(page.getByTestId('lobby-revision')).toContainText('4')

  // After the bad 304, failure #2 requires 60..72s retry. A fresh focus
  // burst after 30s must not bypass the consumed one-off wake allowance.
  await page.clock.fastForward(35_000)
  await page.evaluate(() => {
    for (let i = 0; i < 30; i++) {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
    }
  })
  await page.clock.fastForward(15_000)
  expect(reads).toBe(2)
  await page.clock.fastForward(25_000)
  await expect.poll(() => reads).toBe(3)

  // Only the later authorized 304 with an unambiguous no-store policy may
  // mark the saved revision current. No revoked identity or room is invented.
  await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
  await expect(page.getByTestId('lobby-revision')).toContainText('4')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-player')).toContainText(actor)
  expect(validators).toEqual(['"rev-4"', '"rev-4"', '"rev-4"'])
  expect(writes).toBe(2) // never replay mutating requests during polling
})

for (const lateStatus of [200, 304] as const) {
  test(`offline aborts an in-flight roster GET and ignores a late HTTP ${lateStatus}`, async ({ page }) => {
    test.setTimeout(65_000)
    await page.clock.install()
    const code = 'room-' + '9'.repeat(32)
    const actor = 'guest-' + '9'.repeat(32)
    const snapshot = {
      lobbyId: code, revision: 1, teamIds: ['white', 'black'],
      participants: [{ playerId: actor, teamId: 'white', heroId: null }],
    }
    let signedIn = false
    let writes = 0
    let newReads = 0

    await page.route('**/api/sessions/me', route => route.fulfill(signedIn
      ? { status: 200, json: { kind: 'guest', playerId: actor } }
      : { status: 401 }))
    await page.route('**/api/sessions/guest', route => {
      if (route.request().method() !== 'POST') return route.continue()
      signedIn = true
      writes++
      return route.fulfill({ status: 200, json: { kind: 'guest', playerId: actor } })
    })
    await page.route('**/api/lobbies', route => {
      if (route.request().method() !== 'POST') return route.continue()
      writes++
      return route.fulfill({ status: 201, json: snapshot })
    })
    await page.route('**/api/lobbies/' + code + '/roster/peek', route => {
      newReads++
      return route.fulfill({
        status: 304,
        headers: { ETag: '"rev-1"', 'Cache-Control': 'no-store' },
      })
    })

    await page.goto('/')
    // Model an abort-IGNORING fetch transport, stronger than actual browser
    // fetch. It may deliver a stale 200/304 after offline and after abort.
    // Only this first passive GET is intercepted; all other HTTP is real
    // browser traffic routed through Playwright above.
    await page.evaluate(() => {
      const state = window as typeof window & {
        __oldPeekStarted?: boolean
        __oldPeekSignal?: AbortSignal
        __finishOldPeek?: (response: Response) => void
      }
      const fetchOriginal = window.fetch.bind(window)
      let intercepted = false
      window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
        const url = typeof input === 'string' ? input
          : input instanceof Request ? input.url : String(input)
        if (!intercepted && url.endsWith('/roster/peek')) {
          intercepted = true
          state.__oldPeekStarted = true
          state.__oldPeekSignal = init?.signal ?? undefined
          return new Promise<Response>(resolve => {
            state.__finishOldPeek = resolve
          })
        }
        return fetchOriginal(input, init)
      }
    })

    await page.getByTestId('lobby-sign-in').click()
    await page.getByTestId('lobby-create').click()
    await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
    expect(writes).toBe(2)

    await page.clock.fastForward(15_100)
    await page.waitForFunction(() =>
      (window as typeof window & { __oldPeekStarted?: boolean }).__oldPeekStarted)
    expect(newReads).toBe(0)

    await page.evaluate(() => window.dispatchEvent(new Event('offline')))
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    expect(await page.evaluate(() =>
      (window as typeof window & { __oldPeekSignal?: AbortSignal })
        .__oldPeekSignal?.aborted)).toBe(true)

    // Deliver the OLD response despite the aborted signal: it must not
    // restore freshness or change the cached roster revision while offline.
    await page.evaluate(({ lateStatus, snapshot }) => {
      const state = window as typeof window & {
        __finishOldPeek?: (response: Response) => void
      }
      const headers = { 'Cache-Control': 'no-store', ETag: '"rev-1"' }
      state.__finishOldPeek?.(lateStatus === 304
        ? new Response(null, { status: 304, headers })
        : new Response(JSON.stringify(snapshot), {
          status: 200, headers: { ...headers, 'Content-Type': 'application/json' },
        }))
    }, { lateStatus, snapshot })
    await page.clock.runFor(200)
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    await expect(page.getByTestId('lobby-revision')).toContainText('1')
    expect(newReads).toBe(0)

    // Only a NEW, verified member-only GET after reconnect can clear stale.
    await page.evaluate(() => window.dispatchEvent(new Event('online')))
    await page.clock.fastForward(15_100)
    await expect.poll(() => newReads).toBe(1)
    await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
    await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
    await expect(page.getByTestId('lobby-player')).toContainText(actor)
    expect(writes).toBe(2) // no automatic session issuance or room mutations
  })
}

for (const readKind of ['manual', 'conflict'] as const) {
  for (const lateStatus of [200, 401, 404] as const) {
    test(`ignores pre-offline explicit roster ${readKind} GET late HTTP ${lateStatus}`, async ({ page }) => {
      test.setTimeout(65_000)
      // Freeze passive polling so only the member-initiated GET or the 409
      // recovery GET is under test. The transport intentionally ignores
      // AbortSignal, just as a slow/misbehaving edge may do.
      await page.clock.install()
      const code = 'room-' + 'e'.repeat(32)
      const actor = 'guest-' + 'e'.repeat(32)
      const initial = {
        lobbyId: code, revision: 1, teamIds: ['white', 'black'],
        participants: [{ playerId: actor, teamId: 'white', heroId: null }],
      }
      const next = { ...initial, revision: 2 }
      let signedIn = false
      let writes = 0
      let conflictWrites = 0
      let freshReads = 0

      await page.route('**/api/sessions/me', route => route.fulfill(signedIn
        ? { status: 200, json: { kind: 'guest', playerId: actor } }
        : { status: 401 }))
      await page.route('**/api/sessions/guest', route => {
        if (route.request().method() !== 'POST') return route.continue()
        signedIn = true
        writes++
        return route.fulfill({ status: 200, json: { kind: 'guest', playerId: actor } })
      })
      await page.route('**/api/lobbies', route => {
        if (route.request().method() !== 'POST') return route.continue()
        writes++
        return route.fulfill({ status: 201, json: initial })
      })
      await page.route('**/api/lobbies/' + code + '/roster/peek', route =>
        route.fulfill({
          status: 304,
          headers: {
            ETag: route.request().headers()['if-none-match'] ?? '"rev-1"',
            'Cache-Control': 'no-store',
          },
        }))
      await page.route('**/api/lobbies/' + code + '/roster', route => {
        freshReads++
        return route.fulfill({ status: 200, json: next })
      })
      await page.route('**/api/lobbies/' + code + '/team', route => {
        conflictWrites++
        return route.fulfill({
          status: 409,
          headers: { 'Cache-Control': 'no-store' },
        })
      })

      await page.goto('/')
      // Hold only the FIRST explicit member roster GET in a JS Promise.
      // Unlike normal fetch, it can resolve after an offline transition.
      await page.evaluate(() => {
        const state = window as typeof window & {
          __oldExplicitReadStarted?: boolean
          __finishOldExplicitRead?: (response: Response) => void
        }
        const original = window.fetch.bind(window)
        let intercepted = false
        window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
          const url = typeof input === 'string' ? input
            : input instanceof Request ? input.url : String(input)
          if (!intercepted && url.includes('/api/lobbies/') && url.endsWith('/roster')) {
            intercepted = true
            state.__oldExplicitReadStarted = true
            return new Promise<Response>(resolve => {
              state.__finishOldExplicitRead = resolve
            })
          }
          return original(input, init)
        }
      })

      await page.getByTestId('lobby-sign-in').click()
      await page.getByTestId('lobby-create').click()
      await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
      await expect(page.getByTestId('lobby-revision')).toContainText('1')
      await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)

      if (readKind === 'manual')
        await page.getByTestId('lobby-refresh').click()
      else
        await page.getByTestId('lobby-change-team').click()
      await page.waitForFunction(() =>
        (window as typeof window & {
          __oldExplicitReadStarted?: boolean
        }).__oldExplicitReadStarted === true)

      await page.evaluate(() => window.dispatchEvent(new Event('offline')))
      await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()

      // Deliver a previously requested 200 or even 401/404 *after*
      // offline. It must not replace the room or revoke the known guest.
      await page.evaluate(({ lateStatus, next }) => {
        const state = window as typeof window & {
          __finishOldExplicitRead?: (response: Response) => void
        }
        const response = lateStatus === 200
          ? new Response(JSON.stringify(next), {
            status: 200, headers: { 'Content-Type': 'application/json' },
          })
          : new Response(null, { status: lateStatus })
        state.__finishOldExplicitRead?.(response)
      }, { lateStatus, next })
      await expect(page.getByTestId(
        readKind === 'manual' ? 'lobby-refresh' : 'lobby-change-team',
      )).toBeEnabled()
      await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
      await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
      await expect(page.getByTestId('lobby-revision')).toContainText('1')
      await expect(page.getByTestId('lobby-player')).toContainText(actor)
      expect(freshReads).toBe(0)
      expect(writes).toBe(2)
      expect(conflictWrites).toBe(readKind === 'conflict' ? 1 : 0)

      // Only a *new* verified GET, initiated after reconnect, may bring
      // the room to revision 2 and remove the stale warning.
      await page.evaluate(() => window.dispatchEvent(new Event('online')))
      await page.getByTestId('lobby-refresh').click()
      await expect(page.getByTestId('lobby-revision')).toContainText('2')
      await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
      await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
      await expect(page.getByTestId('lobby-player')).toContainText(actor)
      expect(freshReads).toBe(1)
      expect(writes).toBe(2)
      expect(conflictWrites).toBe(readKind === 'conflict' ? 1 : 0)
    })
  }
}

for (const action of ['hero', 'team'] as const) {
  test(`keeps a late successful room mutation stale across offline (${action})`, async ({ page }) => {
    test.setTimeout(65_000)
    await page.clock.install()
    const code = 'room-' + '6'.repeat(32)
    const actor = 'guest-' + '6'.repeat(32)
    const initial = {
      lobbyId: code, revision: 1, teamIds: ['white', 'black'],
      participants: [{ playerId: actor, teamId: 'white', heroId: null }],
    }
    const updated = {
      ...initial, revision: 2,
      participants: [{ ...initial.participants[0],
        teamId: action === 'team' ? 'black' : 'white',
        heroId: action === 'hero' ? 'kira' : null }],
    }
    let signedIn = false
    let signIns = 0
    let creates = 0
    let mutationNetworkCalls = 0
    let memberReads = 0
    await page.route('**/api/sessions/me', route => route.fulfill(signedIn
      ? { status: 200, json: { kind: 'guest', playerId: actor } }
      : { status: 401 }))
    await page.route('**/api/sessions/guest', route => {
      if (route.request().method() !== 'POST') return route.continue()
      signedIn = true
      signIns++
      return route.fulfill({ status: 200, json: { kind: 'guest', playerId: actor } })
    })
    await page.route('**/api/lobbies', route => {
      if (route.request().method() !== 'POST') return route.continue()
      creates++
      return route.fulfill({ status: 201, json: initial })
    })
    await page.route('**/api/lobbies/' + code + '/roster/peek', route =>
      route.fulfill({ status: 304, headers: {
        ETag: route.request().headers()['if-none-match'] ?? '"rev-1"',
        'Cache-Control': 'no-store',
      } }))
    await page.route('**/api/lobbies/' + code + '/roster', route => {
      memberReads++
      return route.fulfill({ status: 200, json: updated })
    })
    await page.route('**/api/lobbies/' + code + '/' + action, route => {
      // The first PATCH is intercepted in page JS, so this route must
      // never be hit unless the client wrongly replays the mutation.
      mutationNetworkCalls++
      return route.fulfill({ status: 200, json: updated })
    })

    await page.goto('/')
    // Model a transport that keeps the PATCH response pending even after
    // offline, then resolves it with an authoritative, validated 200.
    // The application must keep its verified revision but *not* claim
    // post-disconnect freshness; do not abort or replay the player's PATCH.
    await page.evaluate(action => {
      const state = window as typeof window & {
        __heldMutationStarted?: boolean
        __heldMutationCount?: number
        __releaseHeldMutation?: (body: object) => void
      }
      const original = window.fetch.bind(window)
      let intercepted = false
      window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
        const url = typeof input === 'string' ? input
          : input instanceof Request ? input.url : String(input)
        if (!intercepted && url.endsWith('/' + action) && init?.method === 'PATCH') {
          intercepted = true
          state.__heldMutationCount = (state.__heldMutationCount ?? 0) + 1
          state.__heldMutationStarted = true
          return new Promise<Response>(resolve => {
            state.__releaseHeldMutation = body => resolve(new Response(
              JSON.stringify(body), { status: 200,
                headers: { 'Content-Type': 'application/json' } },
            ))
          })
        }
        return original(input, init)
      }
    }, action)
    await page.getByTestId('lobby-sign-in').click()
    await page.getByTestId('lobby-create').click()
    await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
    await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
    if (action === 'team')
      await page.getByTestId('lobby-new-team').selectOption('black')
    await page.getByTestId(action === 'team' ? 'lobby-change-team' : 'lobby-pick-hero').click()
    await page.waitForFunction(() => (window as typeof window & {
      __heldMutationStarted?: boolean
    }).__heldMutationStarted === true)
    await page.evaluate(() => window.dispatchEvent(new Event('offline')))
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    await page.evaluate(updated => {
      (window as typeof window & {
        __releaseHeldMutation?: (body: object) => void
      }).__releaseHeldMutation?.(updated)
    }, updated)
    await expect(page.getByTestId('lobby-revision')).toContainText('2')
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
    await expect(page.getByTestId('lobby-player')).toContainText(actor)
    if (action === 'team') await expect(page.getByTestId('lobby-roster')).toContainText('black')
    await expect(page.getByTestId('lobby-change-team')).toBeEnabled()
    expect(memberReads).toBe(0)
    expect(mutationNetworkCalls).toBe(0)
    expect(signIns).toBe(1)
    expect(creates).toBe(1)
    expect(await page.evaluate(() => (window as typeof window & {
      __heldMutationCount?: number
    }).__heldMutationCount)).toBe(1)

    // A later, explicitly authorized GET, initiated after reconnect, can
    // certify that revision as current without replaying the PATCH.
    await page.evaluate(() => window.dispatchEvent(new Event('online')))
    await page.getByTestId('lobby-refresh').click()
    await expect(page.getByTestId('lobby-roster-stale')).toHaveCount(0)
    await expect(page.getByTestId('lobby-revision')).toContainText('2')
    expect(memberReads).toBe(1)
    expect(mutationNetworkCalls).toBe(0)
    expect(signIns).toBe(1)
    expect(creates).toBe(1)
  })
}

for (const oldResult of ['401', 'different-guest', 'other-kind'] as const) {
  test(`ignores a pre-offline guest session recheck (${oldResult}) and verifies fresh 401 after reconnect`, async ({ page }) => {
    test.setTimeout(65_000)
    await page.clock.install()
    const roomId = 'room-' + 'f'.repeat(32)
    const playerId = 'guest-' + 'f'.repeat(32)
    const otherGuest = 'guest-' + 'd'.repeat(32)
    const room = {
      lobbyId: roomId, revision: 1, teamIds: ['white', 'black'],
      participants: [{ playerId, teamId: 'white', heroId: null }],
    }
    let signedIn = false
    let currentStatus: 200 | 401 = 200
    let serverChecks = 0
    let guestPosts = 0
    let roomCreates = 0
    await page.route('**/api/sessions/me', route => {
      serverChecks++
      if (!signedIn || currentStatus === 401)
        return route.fulfill({ status: 401, headers: { 'Cache-Control': 'no-store' } })
      return route.fulfill({ status: 200,
        json: { kind: 'guest', playerId } })
    })
    await page.route('**/api/sessions/guest', route => {
      if (route.request().method() !== 'POST') return route.continue()
      signedIn = true
      guestPosts++
      return route.fulfill({ status: 200, json: { kind: 'guest', playerId } })
    })
    await page.route('**/api/lobbies', route => {
      if (route.request().method() !== 'POST') return route.continue()
      roomCreates++
      return route.fulfill({ status: 201, json: room })
    })
    await page.route('**/api/lobbies/' + roomId + '/roster/peek', route =>
      route.fulfill({ status: 304,
        headers: {
          ETag: route.request().headers()['if-none-match'] ?? '"rev-1"',
          'Cache-Control': 'no-store',
        },
      }))

    await page.goto('/')
    await page.getByTestId('lobby-sign-in').click()
    await page.getByTestId('lobby-create').click()
    await expect(page.getByTestId('lobby-room-code')).toHaveText(roomId)
    const baselineChecks = serverChecks
    expect(guestPosts).toBe(1)
    expect(roomCreates).toBe(1)

    // Hold only the next session GET; its fetch disregards AbortSignal and
    // returns an authoritative-looking stale 401 or changed identity.
    await page.evaluate(() => {
      const state = window as typeof window & {
        __oldSessionStarted?: boolean
        __releaseOldSession?: (response: Response) => void
      }
      const original = window.fetch.bind(window)
      let intercepted = false
      window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
        const url = typeof input === 'string' ? input
          : input instanceof Request ? input.url : String(input)
        if (!intercepted && url.endsWith('/api/sessions/me')) {
          intercepted = true
          state.__oldSessionStarted = true
          return new Promise<Response>(resolve => {
            state.__releaseOldSession = resolve
          })
        }
        return original(input, init)
      }
    })
    await page.evaluate(() => window.dispatchEvent(new Event('focus')))
    await page.waitForFunction(() => (window as typeof window & {
      __oldSessionStarted?: boolean
    }).__oldSessionStarted === true)
    expect(serverChecks).toBe(baselineChecks)

    await page.evaluate(() => window.dispatchEvent(new Event('offline')))
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    // Offline hint storms cannot spend Redis lookups or queue 50 GETs.
    await page.evaluate(() => {
      const channel = new BroadcastChannel('shirokuro:guest-session-changed')
      for (let i = 0; i < 50; i++) {
        window.dispatchEvent(new Event('focus'))
        channel.postMessage('recheck')
      }
      channel.close()
    })
    await page.clock.fastForward(8_000)
    expect(serverChecks).toBe(baselineChecks)

    await page.evaluate(({ oldResult, otherGuest }) => {
      const state = window as typeof window & {
        __releaseOldSession?: (response: Response) => void
      }
      let response: Response
      if (oldResult === '401') {
        response = new Response(null, {
          status: 401, headers: { 'Cache-Control': 'no-store' },
        })
      } else {
        response = new Response(JSON.stringify(oldResult === 'different-guest'
          ? { kind: 'guest', playerId: otherGuest }
          : { kind: 'other', playerId: 'external-authenticated-user' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' },
        })
      }
      state.__releaseOldSession?.(response)
    }, { oldResult, otherGuest })
    await page.clock.runFor(100)
    await expect(page.getByTestId('lobby-player')).toContainText(playerId)
    await expect(page.getByTestId('lobby-room-code')).toHaveText(roomId)
    await expect(page.getByTestId('lobby-revision')).toContainText('1')
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    expect(serverChecks).toBe(baselineChecks)

    // All offline cross-tab/focus hints are coalesced: just one new
    // server-authorized /me on online, not one per hint.
    await page.evaluate(() => window.dispatchEvent(new Event('online')))
    await page.clock.runFor(100)
    await expect.poll(() => serverChecks).toBe(baselineChecks + 1)
    await expect(page.getByTestId('lobby-player')).toContainText(playerId)
    await expect(page.getByTestId('lobby-room-code')).toHaveText(roomId)
    await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
    expect(guestPosts).toBe(1)
    expect(roomCreates).toBe(1)

    // A fresh 401 started after reconnection IS authoritative and revokes
    // the existing guest, unlike its pre-disconnect predecessor.
    currentStatus = 401
    await page.clock.fastForward(2_100)
    await page.evaluate(() => window.dispatchEvent(new Event('focus')))
    await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
    await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
    await expect(page.getByTestId('lobby-player')).toHaveCount(0)
    expect(serverChecks).toBe(baselineChecks + 2)
    expect(guestPosts).toBe(1)
    expect(roomCreates).toBe(1)
  })
}
