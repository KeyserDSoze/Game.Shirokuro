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
  await page.clock.fastForward(15_000)
  await expect.poll(() => reads).toBe(3)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-roster-stale')).toBeVisible()
  await expect(page.getByTestId('lobby-player')).toContainText(actor)
  expect(mutations).toBe(2) // never replay POST/PATCH/DELETE due to a hint
})
