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
  page.on('request', request => {
    if (['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method()) &&
        new URL(request.url()).pathname.startsWith('/api/')) {
      writes.push(request.url())
    }
  })

  // Normal first passive poll fails on 503, retaining the verified room.
  await expect.poll(() => reads, { timeout: 25_000 }).toBe(1)
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
})
