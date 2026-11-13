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

    // Register BEFORE the second player's mutation. Otherwise the first
    // browser can poll revision 4 before Playwright starts observing requests,
    // making a successful read appear as a missing request (flaky #109).
    // The observer sees only transport metadata, never roster contents.
    const passiveReads: string[] = []
    first.on('request', request => {
      if (request.url().includes('/api/lobbies/' + invite + '/roster/peek'))
        passiveReads.push(request.method())
    })
    await second.getByRole('button', { name: 'Seleziona Shilla' }).click()
    await second.getByTestId('lobby-pick-hero').click()
    await expect(second.getByTestId('lobby-message')).toContainText('Eroe Shilla salvato')
    // Automatic, authenticated, read-only no-heartbeat GET. No user click
    // on Refresh; only the explicit button is allowed to touch inactivity.
    await first.bringToFront()
    await expect(first.getByTestId('lobby-revision')).toContainText('4', { timeout: 25_000 })
    await expect(first.getByTestId('lobby-roster')).toContainText('Shilla')
    await expect(first.getByTestId('lobby-roster')).toContainText('Kira')
    expect(passiveReads).toContain('GET')
    expect(passiveReads.every(method => method === 'GET')).toBe(true)

    // Confirm the write on the authoritative server, not just UI text.
    // If a later passive GET races the 204, the leave confirmation must
    // remain visible; a genuine HTTP 404 still fails the test.
    await second.bringToFront()
    await expect(second.getByTestId('lobby-revision')).toContainText('4')
    const leaveReply = second.waitForResponse(reply =>
      reply.request().method() === 'DELETE' &&
      reply.url().includes('/api/lobbies/' + invite + '/membership'))
    await second.getByTestId('lobby-leave').click()
    expect((await leaveReply).status()).toBe(204)
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

test('pending invitation clipboard cannot overwrite confirmed room loss feedback', async ({ page }) => {
  const actor = 'guest-' + '7'.repeat(32)
  const roomIds = ['7', '8', '9'].map(char => 'room-' + char.repeat(32))
  let signedIn = false
  let guestPosts = 0
  let creates = 0
  let leaves = 0
  let reads = 0
  let teamWrites = 0

  // Real clipboard permissions are covered elsewhere. Fake only delayed
  // clipboard outcomes and HTTP so this test consumes no guest IP quota.
  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { playerId: actor, kind: 'guest' } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', route => {
    guestPosts++
    signedIn = true
    return route.fulfill({ status: 200, json: { playerId: actor, kind: 'guest' } })
  })
  await page.route('**/api/lobbies', route => {
    if (route.request().method() !== 'POST') return route.continue()
    const lobbyId = roomIds[creates++]
    return route.fulfill({ status: 201, json: {
      lobbyId, revision: 1, teamIds: ['white', 'black'],
      participants: [{ playerId: actor, teamId: 'white', heroId: null }],
    } })
  })
  await page.route('**/api/lobbies/*/membership', route => {
    leaves++
    return route.fulfill({ status: 204 })
  })
  await page.route('**/api/lobbies/*/roster', route => {
    reads++
    return route.fulfill({ status: 404 })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[0])

  // A successful clipboard permission result arriving after DELETE 204
  // cannot replace the authenticated 'left lobby' status (#118).
  await page.evaluate(() => {
    const w = window as Window & { finishCopy?: () => void }
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
      writeText: () => new Promise<void>(resolve => { w.finishCopy = resolve }),
    } })
  })
  await page.getByTestId('lobby-copy-invite').click()
  await page.waitForFunction(() =>
    typeof (window as Window & { finishCopy?: () => void }).finishCopy === 'function')
  await page.getByTestId('lobby-leave').click()
  await expect(page.getByTestId('lobby-message')).toContainText('lasciato la lobby')
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  await page.evaluate(async () => {
    (window as Window & { finishCopy?: () => void }).finishCopy?.()
    await new Promise(resolve => setTimeout(resolve, 0))
  })
  await expect(page.getByTestId('lobby-message')).toContainText('lasciato la lobby')

  // Also ignore late *rejection* when a member-only roster GET returned 404.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[1])
  await page.evaluate(() => {
    const w = window as Window & { finishCopy?: () => void }
    delete w.finishCopy
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
      writeText: () => new Promise<void>((_resolve, reject) => {
        w.finishCopy = () => reject(new Error('Late clipboard denial'))
      }),
    } })
  })
  await page.getByTestId('lobby-copy-invite').click()
  await page.waitForFunction(() =>
    typeof (window as Window & { finishCopy?: () => void }).finishCopy === 'function')
  await page.getByTestId('lobby-refresh').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby non disponibile')
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  await page.evaluate(async () => {
    (window as Window & { finishCopy?: () => void }).finishCopy?.()
    await new Promise(resolve => setTimeout(resolve, 0))
  })
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby non disponibile')

  // An active mutation must temporarily disable copying the room invite;
  // a 503 does not revoke the verified room or its copy control.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[2])
  let releaseTeam: (() => void) | undefined
  await page.route('**/api/lobbies/*/team', async route => {
    teamWrites++
    await new Promise<void>(resolve => { releaseTeam = resolve })
    await route.fulfill({ status: 503 })
  })
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-copy-invite')).toBeDisabled()
  await expect.poll(() => teamWrites).toBe(1)
  releaseTeam?.()
  await expect(page.getByTestId('lobby-message')).toContainText('temporaneamente non disponibile')
  await expect(page.getByTestId('lobby-copy-invite')).toBeEnabled()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[2])
  await expect(page.getByTestId('lobby-player')).toContainText(actor)

  expect(guestPosts).toBe(1)
  expect(creates).toBe(3)
  expect(leaves).toBe(1)
  expect(reads).toBe(1)
  expect(teamWrites).toBe(1)
})

test('malformed 200 session envelopes never create a guest or revoke a verified lobby', async ({ page }) => {
  const code = 'room-' + 'c'.repeat(32)
  const playerId = 'guest-' + 'c'.repeat(32)
  const roster = {
    lobbyId: code, revision: 1, teamIds: ['white', 'black'],
    participants: [{ playerId, teamId: 'white', heroId: null }],
  }

  // Mock the boundary without consuming shared Development guest quotas.
  let signedIn = false
  let validIssuance = false
  let meResponse: unknown = { playerId, kind: 'guest' }
  let sessionReads = 0
  let guestPosts = 0
  let roomCreates = 0
  await page.route('**/api/sessions/me', async route => {
    sessionReads++
    await route.fulfill(signedIn
      ? { status: 200, json: meResponse }
      : { status: 401 })
  })
  await page.route('**/api/sessions/guest', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    guestPosts++
    signedIn = true
    await route.fulfill({ status: 200, json: validIssuance
      ? { playerId, kind: 'guest' }
      : { playerId: { forged: playerId }, kind: 'guest', sessionToken: 'private' } })
  })
  await page.route('**/api/lobbies', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    roomCreates++
    await route.fulfill({ status: 201, json: roster })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-message')).toContainText('temporaneamente non disponibile')
  await expect(page.getByTestId('lobby-player')).toHaveCount(0)
  await expect(page.getByTestId('lobby-create')).toHaveCount(0)
  expect(roomCreates).toBe(0)
  expect(guestPosts).toBe(1)

  validIssuance = true
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  const existingRoster = await page.getByTestId('lobby-roster').textContent()
  expect(roomCreates).toBe(1)

  // A bad /me envelope after focus is not a confirmed 401.
  meResponse = { playerId: 'guest-unrelated', kind: 'guest', privatePath: 'hidden' }
  const before = sessionReads
  const refresh = page.waitForResponse(response =>
    response.url().endsWith('/api/sessions/me') && response.status() === 200)
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await refresh
  await expect.poll(() => sessionReads).toBeGreaterThan(before)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await expect(page.getByTestId('lobby-roster')).toHaveText(existingRoster!)
  expect(guestPosts).toBe(2)
  expect(roomCreates).toBe(1)

  // An explicit, valid non-guest session remains a genuine identity change.
  meResponse = { playerId: 'account-id', kind: 'other' }
  const recheck = page.waitForResponse(response =>
    response.url().endsWith('/api/sessions/me') && response.status() === 200)
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await recheck
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-roster')).toHaveCount(0)
  expect(guestPosts).toBe(2)
  expect(roomCreates).toBe(1)
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

  // A 200 misrouted to another room, missing our member, containing
  // noncanonical IDs, unknown heroes or private fields cannot replace the
  // verified roster (#110/#114/#117), even if our actor remains present.
  const verifiedResponse = await page.request.get('/api/lobbies/' + existingRoom + '/roster')
  expect(verifiedResponse.status()).toBe(200)
  const verified = await verifiedResponse.json()
  const priorView = await page.getByTestId('lobby-roster').textContent()
  const verifiedRevision = await page.getByTestId('lobby-revision').textContent()
  let invalidPayload: unknown = null
  await page.route('**/api/lobbies/' + existingRoom + '/roster', async route =>
    route.fulfill({ status: 200, json: invalidPayload }))
  for (const response of [
    { ...verified, lobbyId: 'room-' + 'f'.repeat(32), revision: verified.revision + 20 },
    { ...verified, participants: [{ playerId: 'guest-unrelated', teamId: 'white', heroId: null }] },
    { ...verified, participants: [null] },
    { ...verified, participants: [...verified.participants,
      { playerId: 'guest-noncanonical', teamId: 'white', heroId: null }] },
    { ...verified, participants: [...verified.participants,
      { playerId: 'guest-' + 'A'.repeat(32), teamId: 'black', heroId: null }] },
    // These 200s preserve the legitimate actor and canonical participant
    // identities, but carry hero IDs the server catalog cannot issue (#117).
    { ...verified, participants: verified.participants.map(
      (member: { playerId: string; teamId: string; heroId: string | null }) =>
        ({ ...member, heroId: 'invented-character' })) },
    { ...verified, participants: [...verified.participants,
      { playerId: 'guest-' + 'c'.repeat(32), teamId: 'white', heroId: 'Kira' }] },
    { ...verified, participants: [...verified.participants,
      { playerId: 'guest-' + 'd'.repeat(32), teamId: 'black', heroId: 'kira'.repeat(10_000) }] },
    { ...verified, secretRoute: 'should-not-be-imported' },
    { ...verified, revision: 0 },
  ]) {
    invalidPayload = response
    await page.getByTestId('lobby-refresh').click()
    await expect(page.getByTestId('lobby-message')).toContainText('temporaneamente non disponibile')
    await expect(page.getByTestId('lobby-room-code')).toHaveText(existingRoom)
    await expect(page.getByTestId('lobby-revision')).toHaveText(verifiedRevision!)
    await expect(page.getByTestId('lobby-roster')).toHaveText(priorView!)
  }
  await page.unroute('**/api/lobbies/' + existingRoom + '/roster')

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

test('404 room loss and confirmed 204 leave erase stale invite, not guest identity', async ({ page }) => {
  const actor = 'guest-' + '6'.repeat(32)
  const roomIds = ['1', '2', '3', '4'].map(hex => 'room-' + hex.repeat(32))
  const unknownInvite = 'room-' + '9'.repeat(32)
  let signedIn = false
  let guestPosts = 0
  let created = 0
  let teamWrites = 0
  let joinWrites = 0
  let rosterReads = 0
  let leaves = 0
  let teamStatus = 404

  // Fake only this browser scenario; no real guest issuance/IP quota.
  await page.route('**/api/sessions/me', route => route.fulfill(signedIn
    ? { status: 200, json: { playerId: actor, kind: 'guest' } }
    : { status: 401 }))
  await page.route('**/api/sessions/guest', route => {
    guestPosts++
    signedIn = true
    return route.fulfill({ status: 200, json: { playerId: actor, kind: 'guest' } })
  })
  await page.route('**/api/lobbies', route => {
    if (route.request().method() !== 'POST') return route.continue()
    const lobbyId = roomIds[created++]
    return route.fulfill({ status: 201, json: {
      lobbyId, revision: 1, teamIds: ['white', 'black'],
      participants: [{ playerId: actor, teamId: 'white', heroId: null }],
    } })
  })
  await page.route('**/api/lobbies/*/team', route => {
    teamWrites++
    return route.fulfill({ status: teamStatus })
  })
  await page.route('**/api/lobbies/*/join', route => {
    joinWrites++
    return route.fulfill({ status: 404 })
  })
  await page.route('**/api/lobbies/*/roster', route => {
    rosterReads++
    return route.fulfill({ status: 404 })
  })
  await page.route('**/api/lobbies/*/membership', route => {
    leaves++
    return route.fulfill({ status: 204 })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-player')).toContainText(actor)

  // 404 from an authenticated current-room mutation clears room and invite.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[0])
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby non disponibile')
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  await expect(page.getByTestId('lobby-player')).toContainText(actor)

  // A rejected join for a never-verified room is different: keep the
  // manually entered invitation editable, with no guest revocation.
  await page.getByTestId('lobby-invite-input').fill(unknownInvite)
  await page.getByTestId('lobby-join').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby non disponibile')
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue(unknownInvite)

  // 409 followed by GET 404 proves the existing room is gone.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[1])
  teamStatus = 409
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby non disponibile')
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  expect(rosterReads).toBe(1)

  // Redis/edge outage preserves the verified room, but 204 leave does not.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[2])
  teamStatus = 503
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('temporaneamente non disponibile')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[2])
  await expect(page.getByTestId('lobby-revision')).toContainText('1')
  await page.getByTestId('lobby-leave').click()
  await expect(page.getByTestId('lobby-message')).toContainText('lasciato la lobby')
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')

  // The member-scoped explicit GET 404 also invalidates the current room.
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(roomIds[3])
  await page.getByTestId('lobby-refresh').click()
  await expect(page.getByTestId('lobby-message')).toContainText('Lobby non disponibile')
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  await expect(page.getByTestId('lobby-player')).toContainText(actor)

  // Exactly the clicks above: no background mutations or new guest tickets.
  expect(guestPosts).toBe(1)
  expect(created).toBe(4)
  expect(teamWrites).toBe(3)
  expect(joinWrites).toBe(1)
  expect(rosterReads).toBe(2)
  expect(leaves).toBe(1)
})

test('409 refresh rejects malformed membership loss and only clears on verified loss', async ({ page }) => {
  const code = 'room-' + 'b'.repeat(32)
  const actorId = 'guest-' + 'b'.repeat(32)
  const otherId = 'guest-' + 'a'.repeat(32)
  const initial = {
    lobbyId: code, revision: 1, teamIds: ['white', 'black'],
    participants: [{ playerId: actorId, teamId: 'white', heroId: null }],
  }
  // The two authentication responses and room creation are mocked: this
  // negative test must not consume real shared-IP guest admission quota.
  let signedIn = false
  let guestPosts = 0
  let roomCreates = 0
  let rejectedChanges = 0
  let conflictReads = 0
  let refreshPayload: unknown = null
  await page.route('**/api/sessions/me', route => route.fulfill(
    signedIn ? { status: 200, json: { playerId: actorId, kind: 'guest' } }
      : { status: 401 },
  ))
  await page.route('**/api/sessions/guest', route => {
    guestPosts++
    signedIn = true
    return route.fulfill({ status: 200, json: { playerId: actorId, kind: 'guest' } })
  })
  await page.route('**/api/lobbies', route => {
    if (route.request().method() !== 'POST') return route.continue()
    roomCreates++
    return route.fulfill({ status: 201, json: initial })
  })
  await page.route('**/api/lobbies/' + code + '/team', route => {
    rejectedChanges++
    return route.fulfill({ status: 409, json: { code: 'revision_conflict' } })
  })
  await page.route('**/api/lobbies/' + code + '/roster', route => {
    conflictReads++
    return route.fulfill({ status: 200, json: refreshPayload })
  })

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await expect(page.getByTestId('lobby-player')).toContainText(actorId)
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  const initialView = await page.getByTestId('lobby-roster').textContent()
  expect(guestPosts).toBe(1)
  expect(roomCreates).toBe(1)

  const invalidReplies: unknown[] = [
    { ...initial, revision: 2, participants: [], secretRoute: 'must-not-be-displayed' },
    { ...initial, revision: 2, participants: [] },
    { ...initial, lobbyId: 'room-' + 'f'.repeat(32), revision: 2,
      participants: [{ playerId: otherId, teamId: 'black', heroId: null }] },
    { ...initial, revision: 0,
      participants: [{ playerId: otherId, teamId: 'black', heroId: null }] },
    { ...initial, revision: 2,
      participants: [{ playerId: 'guest-other', teamId: 'white', heroId: null }] },
  ]
  for (const [index, reply] of invalidReplies.entries()) {
    refreshPayload = reply
    await page.getByTestId('lobby-change-team').click()
    await expect(page.getByTestId('lobby-message')).toContainText('aggiorna il roster')
    await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
    await expect(page.getByTestId('lobby-revision')).toContainText('1')
    await expect(page.getByTestId('lobby-roster')).toHaveText(initialView!)
    await expect(page.getByTestId('lobby-player')).toContainText(actorId)
    expect(conflictReads).toBe(index + 1)
    expect(rejectedChanges).toBe(index + 1)
    expect(guestPosts).toBe(1)
  }

  // A complete, current response with our member is safe to adopt.
  refreshPayload = {
    ...initial, revision: 2,
    participants: [
      { playerId: actorId, teamId: 'white', heroId: null },
      { playerId: otherId, teamId: 'black', heroId: null },
    ],
  }
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-message')).toContainText('roster aggiornato')
  await expect(page.getByTestId('lobby-revision')).toContainText('2')
  await expect(page.getByTestId('lobby-roster').locator('p')).toHaveCount(2)
  expect(conflictReads).toBe(invalidReplies.length + 1)
  expect(rejectedChanges).toBe(invalidReplies.length + 1)

  // Only a structurally valid current roster without the actor is evidence
  // to clear local room and invite. The guest remains signed in.
  refreshPayload = {
    ...initial, revision: 3,
    participants: [{ playerId: otherId, teamId: 'black', heroId: null }],
  }
  await page.getByTestId('lobby-change-team').click()
  await expect(page.getByTestId('lobby-create')).toBeVisible()
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-roster')).toHaveCount(0)
  await expect(page.getByTestId('lobby-player')).toContainText(actorId)
  await expect(page.getByTestId('lobby-invite-input')).toHaveValue('')
  expect(conflictReads).toBe(invalidReplies.length + 2)
  expect(rejectedChanges).toBe(invalidReplies.length + 2)
  expect(guestPosts).toBe(1)
  expect(roomCreates).toBe(1)
})

test('auto-sync rejects malformed 200, retains view on 503 and clears only on authorized 404/401', async ({ page }) => {
  // Bounded backoff after an invalid 200 adds 15s to this end-to-end
  // sequence; subsequent 503 now correctly escalates to 60s rather than 30s.
  test.setTimeout(195_000)
  const code = 'room-' + 'e'.repeat(32)
  const playerId = 'guest-' + 'e'.repeat(32)
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

  // Exercise 304, malformed 200 with outage backoff, 503, 404 and 401
  // without minting identities or issuing automatic writes (#111/#112).
  let status = 304
  let reads = 0
  const validators: (string | undefined)[] = []
  const pollTimes: number[] = []
  await page.route('**/api/lobbies/' + code + '/roster/peek', async route => {
    if (route.request().method() !== 'GET') return route.continue()
    reads++
    pollTimes.push(Date.now())
    validators.push(route.request().headers()['if-none-match'])
    await route.fulfill({
      status,
      headers: { 'Cache-Control': 'no-store' },
      ...(status === 304 ? {} : status === 200
        ? { json: {
          ...roster, revision: 2, secretRoute: 'must-not-enter-private-state',
          participants: [{ playerId, teamId: 'black', heroId: 'kira' }],
        } }
        : { body: '{}', contentType: 'application/json' }),
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
  expect(validators[0]).toBe('"rev-1"')
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await expect(page.getByTestId('lobby-message')).not.toContainText('scaduta')
  expect(mutations).toEqual([])

  // The 200 would otherwise advance revision and import a private field.
  status = 200
  await expect.poll(() => reads, { timeout: 25_000 }).toBeGreaterThanOrEqual(2)
  expect(validators[1]).toBe('"rev-1"')

  status = 503
  // Malformed 200 must NOT reset the outage counter. The next GET is
  // delayed at least 30s rather than the normal healthy 15s.
  await expect.poll(() => reads, { timeout: 45_000 }).toBeGreaterThanOrEqual(3)
  expect(pollTimes[2] - pollTimes[1]).toBeGreaterThanOrEqual(30_000)
  expect(pollTimes[2] - pollTimes[1]).toBeLessThan(45_000)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  // The next actual request proves the malformed 200 was not adopted;
  // checking the UI immediately after the prior request would be racy.
  expect(validators[2]).toBe('"rev-1"')
  await expect(page.getByTestId('lobby-revision')).toContainText('1')
  await expect(page.getByTestId('lobby-roster')).toContainText('white')
  await expect(page.getByTestId('lobby-roster')).not.toContainText('Kira')
  expect(mutations).toEqual([])

  status = 404
  // A second consecutive bad response (503) escalates the same bounded
  // schedule to 60s, without repeated requests during Redis failover.
  await expect.poll(() => reads, { timeout: 85_000 }).toBeGreaterThanOrEqual(4)
  expect(pollTimes[3] - pollTimes[2]).toBeGreaterThanOrEqual(60_000)
  expect(pollTimes[3] - pollTimes[2]).toBeLessThan(80_000)
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
  await expect.poll(() => reads, { timeout: 25_000 }).toBeGreaterThanOrEqual(5)
  await expect(page.getByTestId('lobby-sign-in')).toBeVisible()
  await expect(page.getByTestId('lobby-room-code')).toHaveCount(0)
  await expect(page.getByTestId('lobby-player')).toHaveCount(0)
  await expect(page.getByTestId('lobby-message')).toContainText('scaduta o revocata')
  expect(mutations).toEqual([])
})

test('an explicit pre-match action aborts a stalled passive GET without revoking the guest', async ({ page }) => {
  test.setTimeout(45_000)
  const code = 'room-' + 'd'.repeat(32)
  const playerId = 'guest-' + 'd'.repeat(32)
  const roster = {
    lobbyId: code, revision: 1, teamIds: ['white', 'black'],
    participants: [{ playerId, teamId: 'white', heroId: null }],
  }
  const changed = {
    ...roster, revision: 2,
    participants: [{ playerId, teamId: 'black', heroId: null }],
  }

  // Deterministic transport-only guest: no real guest issuance or quota usage.
  let signedIn = false
  await page.route('**/api/sessions/me', async route =>
    route.fulfill(signedIn
      ? { status: 200, json: { playerId, kind: 'guest' } }
      : { status: 401 }))
  await page.route('**/api/sessions/guest', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    signedIn = true
    await route.fulfill({ status: 200, json: { playerId, kind: 'guest' } })
  })
  await page.route('**/api/lobbies', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    await route.fulfill({ status: 201, json: roster })
  })
  let mutations = 0
  await page.route('**/api/lobbies/' + code + '/team', async route => {
    mutations++
    await route.fulfill({ status: 200, json: changed })
  })

  // A deliberately unresolved fetch observes actual AbortController behavior,
  // rather than relying on browser-specific network abort event ordering.
  await page.addInitScript(() => {
    const underlyingFetch = window.fetch.bind(window)
    const probe = { started: 0, aborted: 0 }
    Object.defineProperty(window, '__shirokuroPeekProbe', { value: probe })
    window.fetch = (input, init) => {
      if (typeof input === 'string' && input.endsWith('/roster/peek')) {
        probe.started++
        return new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => {
            probe.aborted++
            reject(new DOMException('Aborted', 'AbortError'))
          }, { once: true })
        })
      }
      return underlyingFetch(input, init)
    }
  })
  const pollStats = () => page.evaluate(() =>
    (window as Window & { __shirokuroPeekProbe: { started: number; aborted: number } })
      .__shirokuroPeekProbe)

  await page.goto('/')
  await page.getByTestId('lobby-sign-in').click()
  await page.getByTestId('lobby-create').click()
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  await expect.poll(async () => (await pollStats()).started, { timeout: 25_000 }).toBe(1)

  await page.getByTestId('lobby-new-team').selectOption('black')
  await page.getByTestId('lobby-change-team').click()
  await expect.poll(async () => (await pollStats()).aborted).toBe(1)
  await expect(page.getByTestId('lobby-revision')).toContainText('2')
  await expect(page.getByTestId('lobby-player')).toContainText(playerId)
  await expect(page.getByTestId('lobby-room-code')).toHaveText(code)
  expect(mutations).toBe(1)
  expect((await pollStats()).started).toBe(1) // no immediate retry
})
