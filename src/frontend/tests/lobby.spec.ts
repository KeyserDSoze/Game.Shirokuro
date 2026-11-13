import { expect, test } from '@playwright/test'

test('two independent guest browsers create a room, join by invite and select heroes safely', async ({ browser }) => {
  // End-to-end scenario exercises many real UI/API roundtrips plus R3F across
  // two browser contexts. Give only this long test a realistic CI budget.
  test.setTimeout(115_000)
  const firstContext = await browser.newContext()
  const secondContext = await browser.newContext()
  try {
    const first = await firstContext.newPage()
    const second = await secondContext.newPage()
    await Promise.all([first.goto('/'), second.goto('/')])

    await first.getByTestId('lobby-sign-in').click()
    await second.getByTestId('lobby-sign-in').click()
    await expect(first.getByTestId('lobby-player')).toContainText('guest-')
    await expect(second.getByTestId('lobby-player')).toContainText('guest-')
    const firstId = await first.getByTestId('lobby-player').textContent()
    const secondId = await second.getByTestId('lobby-player').textContent()
    expect(firstId).not.toBe(secondId)

    await first.getByTestId('lobby-create').click()
    await expect(first.getByTestId('lobby-message')).toContainText('Lobby creata')
    const invite = (await first.getByTestId('lobby-room-code').textContent())!.trim()
    expect(invite).toMatch(/^room-[0-9a-f]{32}$/)
    await expect(first.getByTestId('lobby-revision')).toContainText('1')

    await second.getByTestId('lobby-invite-input').fill(invite)
    await second.getByTestId('lobby-join').click()
    await expect(second.getByTestId('lobby-message')).toContainText('Ingresso nella lobby riuscito')
    await expect(second.getByTestId('lobby-room-code')).toHaveText(invite)
    await expect(second.getByTestId('lobby-revision')).toContainText('2')
    await expect(second.getByTestId('lobby-roster').locator('p')).toHaveCount(2)

    // Owner's cached revision is stale after the other player joined.
    await first.getByRole('button', { name: 'Seleziona Kira' }).click()
    await first.getByTestId('lobby-pick-hero').click()
    await expect(first.getByTestId('lobby-message')).toContainText('roster aggiornato')
    await expect(first.getByTestId('lobby-revision')).toContainText('2')
    await expect(first.getByTestId('lobby-roster').locator('p')).toHaveCount(2)
    // The server rejected the stale hero change. The automatic GET must
    // not silently retry it or increase the roster revision to 3.
    await expect(first.getByTestId('lobby-roster')).not.toContainText('Kira')
    await expect(second.getByTestId('lobby-revision')).toContainText('2')
    await first.getByTestId('lobby-pick-hero').click()
    await expect(first.getByTestId('lobby-message')).toContainText('Eroe Kira salvato')

    // Second player reads the changed server roster (not a client-side copy).
    await second.getByTestId('lobby-refresh').click()
    await expect(second.getByTestId('lobby-roster')).toContainText('Kira')
    await expect(second.getByTestId('lobby-revision')).toContainText('3')

    await second.getByRole('button', { name: 'Seleziona Shilla' }).click()
    await second.getByTestId('lobby-pick-hero').click()
    await expect(second.getByTestId('lobby-message')).toContainText('Eroe Shilla salvato')
    // Automatic, authenticated, read-only no-heartbeat GET. No user click
    // on Refresh; only the explicit button is allowed to touch inactivity.
    const passiveReads: string[] = []
    first.on('request', request => {
      if (request.url().includes('/api/lobbies/') &&
          request.url().endsWith('/roster/peek'))
        passiveReads.push(request.method())
    })
    await first.bringToFront()
    await expect(first.getByTestId('lobby-revision')).toContainText('4', { timeout: 25_000 })
    await expect(first.getByTestId('lobby-roster')).toContainText('Shilla')
    await expect(first.getByTestId('lobby-roster')).toContainText('Kira')
    expect(passiveReads).toContain('GET')
    expect(passiveReads.every(method => method === 'GET')).toBe(true)

    await second.getByTestId('lobby-leave').click()
    await expect(second.getByTestId('lobby-message')).toContainText('lasciato la lobby')
    await first.getByTestId('lobby-refresh').click()
    await expect(first.getByTestId('lobby-roster').locator('p')).toHaveCount(1)
    await first.getByTestId('lobby-leave').click()
    await expect(first.getByTestId('lobby-create')).toBeVisible()
  } finally {
    await firstContext.close()
    await secondContext.close()
  }
})

test('guest lobby UI is translated and keyboard-accessible on mobile', async ({ page, context }) => {
  test.setTimeout(60_000)
  await context.grantPermissions(['clipboard-read', 'clipboard-write'])
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByTestId('theme-toggle').click()
  await expect(page.getByRole('heading', { name: 'Guest lobby' })).toBeVisible()
  await page.getByTestId('lobby-sign-in').focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('lobby-player')).toContainText('guest-')

  await page.getByTestId('lobby-teams').fill('white,black,gold')
  await page.getByTestId('lobby-creator-team').fill('gold')
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby created')
  await expect(page.getByTestId('lobby-roster')).toContainText('gold')

  await page.getByTestId('lobby-new-team').selectOption('black')
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-roster')).toContainText('black')

  // Reuse this already-authenticated, real member lobby rather than opening
  // more guest sessions and exhausting the shared Development rate limit.
  const invitation = (await page.getByTestId('lobby-room-code').textContent())!.trim()
  expect(invitation).toMatch(/^room-[0-9a-f]{32}$/)
  await page.getByTestId('lobby-copy-invite').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Invitation code copied')
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(invitation)

  // Explicit clipboard denial leaves the code manually selectable.
  await page.evaluate(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: async () => { throw new Error('Permission denied') } },
    })
  })
  await page.getByTestId('lobby-copy-invite').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Could not copy')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(invitation)

  // A pending clipboard permission result must not change logout feedback.
  await page.evaluate(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        writeText: () => new Promise<void>(resolve => {
          (window as Window & { finishInviteCopy?: () => void }).finishInviteCopy = resolve
        }),
      },
    })
  })
  await page.getByTestId('lobby-copy-invite').click()
  await page.waitForFunction(() =>
    typeof (window as Window & { finishInviteCopy?: () => void }).finishInviteCopy === 'function')
  await page.getByTestId('lobby-sign-out').click()
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(page.getByTestId('lobby-message')).toContainText('signed out')
  await page.evaluate(() =>
    (window as Window & { finishInviteCopy?: () => void }).finishInviteCopy?.())
  await expect(page.getByTestId('lobby-copy-invite')).toHaveCount(0)
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-message')).toContainText('signed out')
  await expect(page.getByTestId('world-stage')).toBeVisible()
})

test('malformed invitation is rejected locally and does not trigger a join request', async ({ page }) => {
  const joined: string[] = []
  page.on('request', request => {
    if (request.url().includes('/api/lobbies/') && request.url().endsWith('/join'))
      joined.push(request.url())
  })
  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-invite-input').fill('../../admin')
  await page.getByTestId('lobby-join').click()
  await expect(page.getByTestId('lobby-message')).toContainText('non valida')
  expect(joined).toEqual([])
})

test('a copied signed guest cookie stops accessing a lobby as soon as its owner signs out', async ({ browser }) => {
  const ownerContext = await browser.newContext()
  const stolenContext = await browser.newContext()
  const secondGuestContext = await browser.newContext()
  try {
    const owner = await ownerContext.newPage()
    await owner.goto('/')
    await owner.getByTestId('lobby-sign-in').click()
    await expect(owner.getByTestId('lobby-player')).toContainText('guest-')
    await owner.getByTestId('lobby-create').click()
    const room = (await owner.getByTestId('lobby-room-code').textContent())!.trim()
    const ownerId = (await owner.getByTestId('lobby-player').textContent())!.trim()
    const second = await secondGuestContext.newPage()
    await second.goto('/')
    await second.getByTestId('lobby-sign-in').click()
    await second.getByTestId('lobby-invite-input').fill(room)
    await second.getByTestId('lobby-join').click()
    await expect(second.getByTestId('lobby-revision')).toContainText('2')
    await expect(second.getByTestId('lobby-roster').locator('p')).toHaveCount(2)

    const originalCookies = await ownerContext.cookies()
    expect(originalCookies.some(cookie => cookie.name === 'Shirokuro.Session')).toBe(true)
    await stolenContext.addCookies(originalCookies)
    const baseUrl = 'http://127.0.0.1:5173'

    const readable = await stolenContext.request.get(baseUrl + '/api/lobbies/' + room + '/roster')
    expect(readable.status()).toBe(200)

    await owner.getByTestId('lobby-sign-out').click()
    await expect(owner.getByTestId('lobby-sign-in')).toBeVisible()

    const revoked = await stolenContext.request.get(baseUrl + '/api/sessions/me')
    expect(revoked.status()).toBe(401)
    const hidden = await stolenContext.request.get(baseUrl + '/api/lobbies/' + room + '/roster')
    expect(hidden.status()).toBe(401)
    const signalr = await stolenContext.request.post(
      baseUrl + '/hubs/lobbies/negotiate?negotiateVersion=1',
    )
    expect(signalr.status()).toBe(401)

    // The remaining authenticated member sees the revised server roster
    // without the signed-out guest, rather than a stale client-side view.
    await second.getByTestId('lobby-refresh').click()
    await expect(second.getByTestId('lobby-revision')).toContainText('3')
    await expect(second.getByTestId('lobby-roster').locator('p')).toHaveCount(1)
    await expect(second.getByTestId('lobby-roster')).not.toContainText(ownerId)
  } finally {
    await ownerContext.close()
    await stolenContext.close()
    await secondGuestContext.close()
  }
})

test('guest refresh can rejoin the same room without minting another identity', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  const firstIdentity = await page.getByTestId('lobby-player').textContent()
  await page.getByTestId('lobby-create').click()
  const code = (await page.getByTestId('lobby-room-code').textContent())!.trim()
  await page.reload()
  await expect(page.getByTestId('lobby-player')).toHaveText(firstIdentity!)
  await page.getByTestId('lobby-invite-input').fill(code)
  await page.getByTestId('lobby-join-team').fill('white')
  await page.getByTestId('lobby-join').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-revision')).toContainText('1')
  await expect(page.getByTestId('lobby-roster').locator('p')).toHaveCount(1)
})

test('guest lobby shows a safe message when creation is rate-limited', async ({ page }) => {
  // Test the browser's 429 behavior without exhausting the shared per-IP
  // throttle used by the rest of the Playwright suite. Real quota boundaries
  // and independent creators are covered by xUnit/TestServer.
  await page.route('**/api/lobbies', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    await route.fulfill({
      status: 429,
      headers: { 'Cache-Control': 'no-store', 'Content-Type': 'application/json' },
      body: '{}',
    })
  })
  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-player')).toContainText('guest-')
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Troppe richieste o lobby piene')
  await expect(page.getByTestId('lobby-create')).toBeVisible()
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
})

test('cross-tab recheck signals are strict and burst-coalesced without guest issuance', async ({ page }) => {
  // This intentionally never creates a guest, so it cannot spend the
  // per-IP 12/min issuance budget shared with other CI browser scenarios.
  let issuancePosts = 0
  page.on('request', request => {
    if (request.method() === 'POST' &&
        request.url().endsWith('/api/sessions/guest'))
      issuancePosts++
  })
  const anonymousReady = page.waitForResponse(response =>
    response.url().endsWith('/api/sessions/me') && response.status() === 401)
  await page.goto('/')
  await anonymousReady
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()

  let checks = 0
  let release!: () => void
  const hold = new Promise<void>(resolve => { release = resolve })
  await page.route('**/api/sessions/me', async route => {
    checks++
    await hold
    await route.fulfill({
      status: 401,
      headers: { 'Cache-Control': 'no-store' },
    })
  })

  // Non-protocol payloads, even a plausible object with a recheck field,
  // must never trigger the Redis-backed authenticated session endpoint.
  await page.evaluate(async () => {
    const channel = new BroadcastChannel('shirokuro:guest-session-changed')
    for (let i = 0; i < 50; i++) {
      channel.postMessage({ action: 'recheck', playerId: 'spoofed' })
      channel.postMessage('recheck ' + i)
      channel.postMessage(null)
    }
    await new Promise(resolve => setTimeout(resolve, 150))
    channel.close()
  })
  expect(checks).toBe(0)

  // 50 real notifications plus a focus event have to share ONE live GET.
  // At most one trailing GET checks an identity change after that response.
  await page.evaluate(async () => {
    const channel = new BroadcastChannel('shirokuro:guest-session-changed')
    for (let i = 0; i < 50; i++) channel.postMessage('recheck')
    window.dispatchEvent(new Event('focus'))
    await new Promise(resolve => setTimeout(resolve, 150))
    channel.close()
  })
  await expect.poll(() => checks).toBe(1)
  release()
  await expect.poll(() => checks).toBe(2)
  // No additional fan-out after the single trailing read has completed.
  await page.waitForTimeout(150)
  expect(checks).toBe(2)
  expect(issuancePosts).toBe(0)
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(page.getByTestId('lobby-player')).toHaveCount(0)
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-roster')).toHaveCount(0)
})

test('guest session revoked in another tab clears private roster on focus without auto-login', async ({ context }) => {
  // Two real guest issuances, shared between cross-tab + standalone focus
  // regressions, avoid exhausting Development's per-IP fixed-window quota.
  test.setTimeout(90_000)
  const owner = await context.newPage()
  await owner.goto('/')

  // The second tab is ALREADY OPEN without a guest before the owner logs
  // in. Wait for its anonymous initial read to complete so adoption cannot
  // be attributed to the initial page-load request.
  const second = await context.newPage()
  const initialAnonymousRead = second.waitForResponse(response =>
    response.url().endsWith('/api/sessions/me') && response.status() === 401)
  await second.goto('/')
  await initialAnonymousRead
  await expect(second.getByTestId('lobby-sign-in')).toBeVisible()
  const secondGuestPosts: string[] = []
  second.on('request', request => {
    if (request.method() === 'POST' && request.url().endsWith('/api/sessions/guest'))
      secondGuestPosts.push(request.url())
  })

  await owner.getByTestId('lobby-sign-in').click()
  await expect(owner.getByTestId('lobby-player')).toContainText('guest-')
  const oldIdentity = (await owner.getByTestId('lobby-player').textContent())!.trim()
  // An opaque broadcast triggers ONLY a GET /me in the previously signed-
  // out tab. It must not issue a new guest or inherit any invitation/roster.
  await expect(second.getByTestId('lobby-player')).toHaveText(oldIdentity)
  expect(secondGuestPosts).toEqual([])
  await expect(second.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(second.getByTestId('lobby-invite-input')).toHaveValue('')

  await owner.getByTestId('lobby-create').click()
  const oldRoom = (await owner.getByTestId('lobby-room-code').textContent())!.trim()
  await expect(second.getByTestId('lobby-room-code')).toHaveCount(0)
  await second.getByTestId('lobby-invite-input').fill(oldRoom)
  await second.getByTestId('lobby-join-team').fill('white')
  await second.getByTestId('lobby-join').click()
  await expect(second.getByTestId('lobby-room-code')).toHaveText(oldRoom)
  await expect(second.getByTestId('lobby-roster').locator('p')).toHaveCount(1)

  // Capture an actual server-authorized 200 roster *before* logout, then
  // withhold its delivery until a DIFFERENT guest has explicitly signed in.
  // React must ignore the obsolete response rather than display old privacy-
  // scoped room data or its old success message under the new identity.
  let captured!: () => void
  let releaseOldResponse!: () => void
  const oldResponseReady = new Promise<void>(resolve => { captured = resolve })
  const deliveryAllowed = new Promise<void>(resolve => { releaseOldResponse = resolve })
  await second.route('**/api/lobbies/' + oldRoom + '/roster', async route => {
    const original = await route.fetch()
    expect(original.status()).toBe(200)
    captured()
    await deliveryAllowed
    await route.fulfill({ response: original })
  })
  await second.getByTestId('lobby-refresh').click()
  await oldResponseReady

  await owner.bringToFront()
  await owner.getByTestId('lobby-sign-out').click()
  await expect(owner.getByTestId('lobby-sign-in')).toBeVisible()

  await second.bringToFront()
  await expect(second.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(second.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(second.getByTestId('lobby-roster')).toHaveCount(0)
  await expect(second.getByTestId('lobby-player')).toHaveCount(0)
  await expect(second.getByTestId('lobby-message')).toContainText('scaduta o revocata')

  // React did not mint another identity during focus. The user must opt in.
  const check = await second.request.get('/api/sessions/me')
  expect(check.status()).toBe(401)
  await second.getByTestId('lobby-sign-in').click()
  await expect(second.getByTestId('lobby-player')).toContainText('guest-')
  expect((await second.getByTestId('lobby-player').textContent())!.trim()).not.toBe(oldIdentity)
  await expect(second.getByTestId('lobby-invite-input')).toHaveValue('')
  await expect(second.getByTestId('lobby-room-code')).toHaveCount(0)
  const deliveredOld = second.waitForResponse(response =>
    response.url().endsWith('/api/lobbies/' + oldRoom + '/roster') &&
    response.status() === 200)
  releaseOldResponse()
  const oldReply = await deliveredOld
  await oldReply.finished()
  await expect(second.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(second.getByTestId('lobby-invite-input')).toHaveValue('')
  await expect(second.getByTestId('lobby-roster')).toHaveCount(0)
  await expect(second.getByTestId('lobby-message')).not.toContainText('Roster aggiornato')

  // After the explicit new login, re-use this guest to cover focus-only
  // recovery without adding another rate-limited sign-in to the CI suite.
  await second.getByTestId('lobby-create').click()
  await expect(second.getByTestId('lobby-room-code')).toBeVisible()
  await context.clearCookies()
  await second.evaluate(() => window.dispatchEvent(new Event('focus')))
  await expect(second.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(second.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(second.getByTestId('lobby-roster')).toHaveCount(0)
  await expect(second.getByTestId('lobby-message')).toContainText('scaduta o revocata')
})

test('401 revokes lobby state, while 400/403/429 and transient HTTP/network errors preserve a valid guest', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-player')).toContainText('guest-')
  await page.getByTestId('lobby-create').click()
  const existingRoom = (await page.getByTestId('lobby-room-code').textContent())!.trim()

  // The same session-check API used by focus/visibility must not turn a
  // transient Redis verification outage (503) into an apparent logout.
  await page.route('**/api/sessions/me', async route => {
    await route.fulfill({ status: 503, headers: { 'Cache-Control': 'no-store' } })
  })
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await expect(page.getByTestId('lobby-room-code')).toHaveText(existingRoom)
  await expect(page.getByTestId('lobby-player')).toContainText('guest-')
  await page.unroute('**/api/sessions/me')

  const initialRevision = await page.getByTestId('lobby-revision').textContent()
  const initialRoster = await page.getByTestId('lobby-roster').textContent()
  let responseStatus = 503
  await page.route('**/api/lobbies/' + existingRoom + '/team', async route => {
    await route.fulfill({
      status: responseStatus,
      headers: { 'Cache-Control': 'no-store' },
      body: '',
    })
  })

  for (const [status, expected] of [
    [502, 'temporaneamente non disponibile'],
    [503, 'temporaneamente non disponibile'],
    [504, 'temporaneamente non disponibile'],
    [400, 'Richiesta non valida'],
    [403, 'Accesso non autorizzato'],
    [429, 'Troppe richieste'],
  ] as const) {
    responseStatus = status
    await page.getByTestId('lobby-change-team').click()
    await expect(page.getByTestId('lobby-message')).toContainText(expected)
    await expect(page.getByTestId('lobby-room-code')).toHaveText(existingRoom)
    await expect(page.getByTestId('lobby-player')).toContainText('guest-')
    await expect(page.getByTestId('lobby-revision')).toHaveText(initialRevision!)
    await expect(page.getByTestId('lobby-roster')).toHaveText(initialRoster!)
  }

  // Unlike a rejected input, a failed network fetch is not evidence of
  // invalid guest identity or server revision. No automatic replay of the
  // denied mutation is permitted; recovery is a separate user click.
  await page.unroute('**/api/lobbies/' + existingRoom + '/team')
  let abortedMutations = 0
  await page.route('**/api/lobbies/' + existingRoom + '/team', async route => {
    abortedMutations++
    await route.abort('failed')
  })
  await page.getByTestId('lobby-new-team').selectOption('black')
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('temporaneamente non disponibile')
  expect(abortedMutations).toBe(1)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(existingRoom)
  await expect(page.getByTestId('lobby-player')).toContainText('guest-')
  await expect(page.getByTestId('lobby-revision')).toHaveText(initialRevision!)
  await expect(page.getByTestId('lobby-roster')).toHaveText(initialRoster!)
  await page.unroute('**/api/lobbies/' + existingRoom + '/team')

  // Server connectivity recovered. Only the explicit new confirmation can
  // commit the team change; a stale response never did it in the background.
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Squadra aggiornata')
  await expect(page.getByTestId('lobby-roster')).toContainText('black')

  // After a 409, an unavailable roster read must not retry the mutation or
  // fabricate a new revision. Avoid extra guest logins in parallel E2E.
  responseStatus = 409
  let conflictReads = 0
  let rejectedWrites = 0
  await page.unroute('**/api/lobbies/' + existingRoom + '/team')
  await page.route('**/api/lobbies/' + existingRoom + '/team', async route => {
    rejectedWrites++
    await route.fulfill({ status: 409, headers: { 'Cache-Control': 'no-store' } })
  })
  await page.route('**/api/lobbies/' + existingRoom + '/roster', async route => {
    conflictReads++
    await route.fulfill({ status: 503, headers: { 'Cache-Control': 'no-store' } })
  })
  const priorRevision = await page.getByTestId('lobby-revision').textContent()
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('aggiorna il roster')
  await expect(page.getByTestId('lobby-revision')).toHaveText(priorRevision!)
  expect(conflictReads).toBe(1)
  expect(rejectedWrites).toBe(1)
  await page.unroute('**/api/lobbies/' + existingRoom + '/roster')
  await page.unroute('**/api/lobbies/' + existingRoom + '/team')

  await page.route('**/api/lobbies/' + existingRoom + '/team', async route => {
    await route.fulfill({ status: 401, headers: { 'Cache-Control': 'no-store' } })
  })
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('scaduta o revocata')
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-roster')).toHaveCount(0)
  await expect(page.getByTestId('lobby-player')).toHaveCount(0)
  // The old invite must not remain available for a newly-issued identity.
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
})

test('auto-sync retains private view on 503, clears room on 404 and guest on 401 without writes', async ({ page }) => {
  test.setTimeout(85_000)
  const code = 'room-' + 'e'.repeat(32)
  const playerId = 'guest-browser-poll'
  const roster = {
    lobbyId: code, revision: 1, teamIds: ['white', 'black'],
    participants: [{ playerId, teamId: 'white', heroId: null }],
  }

  // The full suite runs parallel real logins against a shared Development
  // limiter. This transport-failure test needs no extra guest issuance:
  // use deterministic HTTP responses while the separate two-real-browser
  // test above verifies authenticated cross-client roster updates.
  let signedIn = false
  await page.route('**/api/sessions/me', async route => {
    await route.fulfill(signedIn
      ? { status: 200, json: { playerId, kind: 'guest' } }
      : { status: 401 })
  })
  await page.route('**/api/sessions/guest', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    signedIn = true
    await route.fulfill({ status: 200, json: { playerId, kind: 'guest' } })
  })
  await page.route('**/api/lobbies', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    await route.fulfill({ status: 201, json: roster })
  })

  let status = 503
  let reads = 0
  await page.route('**/api/lobbies/' + code + '/roster/peek', async route => {
    if (route.request().method() !== 'GET') return route.continue()
    reads++
    await route.fulfill({
      status,
      headers: { 'Cache-Control': 'no-store', 'Content-Type': 'application/json' },
      body: '{}',
    })
  })
  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)

  const mutations: string[] = []
  page.on('request', request => {
    if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method()))
      mutations.push(request.url())
  })

  await expect.poll(() => reads, { timeout: 25_000 }).toBeGreaterThanOrEqual(1)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await expect(page.getByTestId('lobby-message')).not.toContainText('scaduta')
  expect(mutations).toEqual([])

  status = 404
  await expect.poll(() => reads, { timeout: 25_000 }).toBeGreaterThanOrEqual(2)
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-create')).toBeVisible()
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  expect(mutations).toEqual([])

  // Reuse the mocked guest to check a later 401 without exceeding any
  // shared real-IP issuance quota or accepting an unauthenticated roster.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  mutations.length = 0
  status = 401
  await expect.poll(() => reads, { timeout: 25_000 }).toBeGreaterThanOrEqual(3)
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-player')).toHaveCount(0)
  await expect(page.getByTestId('lobby-message')).toContainText('scaduta o revocata')
  expect(mutations).toEqual([])
})
