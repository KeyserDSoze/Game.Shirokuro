import { expect, test } from '@playwright/test'

test('shows the technical game shell without claiming an active match', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: /shirokuro/i })).toBeVisible()
  await expect(page.getByTestId('world-stage')).toBeVisible()
  await expect(page.getByTestId('selected-hex')).toContainText('Q')
  await expect(page.getByText('nessuna partita attiva', { exact: false }).first()).toBeVisible()
})

test('switches between light and dark themes', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto('/')
  await expect(page.locator('html')).not.toHaveClass(/dark/)
  await page.getByTestId('theme-toggle').click()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await page.getByTestId('theme-toggle').click()
  await expect(page.locator('html')).not.toHaveClass(/dark/)
})

test('switches language and works on a mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  await expect(page.getByRole('heading', { name: 'The game board' })).toBeVisible()
  await expect(page.getByTestId('world-stage')).toBeVisible()
})


test('lists all fourteen heroes and allows selecting by pointer and keyboard without starting a match', async ({ page }) => {
  await page.goto('/')
  const roster = page.getByRole('group', { name: 'Eroi disponibili' })
  await expect(roster.getByRole('button')).toHaveCount(14)
  await expect(page.getByTestId('selected-hero')).toHaveText('Adsiz')

  await page.getByRole('button', { name: 'Seleziona Kira' }).click()
  await expect(page.getByTestId('selected-hero')).toHaveText('Kira')
  await expect(page.getByTestId('world-hero')).toContainText('Kira')
  await expect(page.getByRole('button', { name: 'Seleziona Kira' })).toHaveAttribute('aria-pressed', 'true')

  const next = page.getByRole('button', { name: 'Seleziona Shilla' })
  await next.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('selected-hero')).toHaveText('Shilla')
  await expect(page.getByText('nessuna partita attiva', { exact: false }).first()).toBeVisible()
})

test('hero picker is responsive and translated on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByRole('button', { name: 'Select Vlko Dlak' }).click()
  await expect(page.getByTestId('selected-hero')).toHaveText('Vlko Dlak')
  await expect(page.getByText('Preview choice only: no active match.')).toBeVisible()
})
 
test('loads the original hero reference atlas and crops the correct character cells', async ({ page }) => {
  const response = await page.request.get('/art/heroes-original-atlas.webp')
  expect(response.ok()).toBeTruthy()
  expect(response.headers()['content-type']).toContain('image/webp')
  expect((await response.body()).length).toBeLessThan(20_000)

  await page.goto('/')
  const kiri = page.getByTestId('hero-kira').getByTestId('hero-art-kira')
  await expect(kiri).toHaveAttribute('data-art', 'original-atlas')
  const kiraCss = await kiri.evaluate(element => {
    const style = window.getComputedStyle(element)
    return { image: style.backgroundImage, position: style.backgroundPosition }
  })
  expect(kiraCss.image).toContain('heroes-original-atlas.webp')
  expect(kiraCss.position).toBe('-280px 0px')

  const last = page.getByTestId('hero-vlko-dlak').getByTestId('hero-art-vlko-dlak')
  await expect(last).toHaveCSS('background-position', '-336px -64px')
  await page.getByRole('button', { name: 'Seleziona Vlko Dlak' }).click()
  await expect(page.getByTestId('selected-hero')).toHaveText('Vlko Dlak')
  await expect(page.getByTestId('world-hero')).toContainText('Vlko Dlak')
})

test('original art remains available on mobile and after changing language and theme', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByTestId('theme-toggle').click()
  const art = page.getByTestId('hero-adsiz').getByTestId('hero-art-adsiz')
  await expect(art).toHaveAttribute('data-art', 'original-atlas')
  await expect(art).toHaveCSS('background-image', /heroes-original-atlas/)
  await page.getByRole('button', { name: 'Select Kylia' }).click()
  await expect(page.getByTestId('selected-hero')).toHaveText('Kylia')
})

test('serves the original six-terrain WebP atlas and displays its swatches', async ({ page }) => {
  const response = await page.request.get('/art/terrain-original-atlas.webp')
  expect(response.ok()).toBeTruthy()
  expect(response.headers()['content-type']).toContain('image/webp')
  expect((await response.body()).length).toBeLessThan(10_000)

  await page.goto('/')
  const types = page.getByRole('group', { name: 'Tipi di terreno' })
  await expect(types.getByRole('button')).toHaveCount(6)
  await expect(page.getByTestId('terrain-current')).toContainText('Città')
  await expect(page.getByTestId('terrain-art-water')).toHaveCSS('background-position', '-48px -48px')
  await expect(page.getByTestId('terrain-art-black')).toHaveCSS('background-image', /terrain-original-atlas/)
})

test('terrain editor paints only the selected local hex and can reset without a match', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Scegli Acqua' }).click()
  await expect(page.getByTestId('terrain-brush-water')).toHaveAttribute('aria-pressed', 'true')
  await page.getByTestId('terrain-paint').click()
  await expect(page.getByTestId('terrain-current')).toContainText('Acqua')

  await page.getByRole('button', { name: 'Scegli Natura' }).focus()
  await page.keyboard.press('Enter')
  await page.getByTestId('terrain-paint').click()
  await expect(page.getByTestId('terrain-current')).toContainText('Natura')

  await page.getByTestId('terrain-reset').click()
  await expect(page.getByTestId('terrain-current')).toContainText('Città')
  await expect(page.getByText('nessuna partita attiva', { exact: false }).first()).toBeVisible()
})

test('terrain editor works on mobile in English and dark mode', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByTestId('theme-toggle').click()
  await expect(page.getByRole('heading', { name: 'Original terrains' })).toBeVisible()
  await page.getByRole('button', { name: 'Choose Sand' }).click()
  await page.getByRole('button', { name: 'Apply terrain' }).click()
  await expect(page.getByTestId('terrain-current')).toContainText('Sand')
  await page.getByRole('button', { name: 'Reset preview' }).click()
  await expect(page.getByTestId('terrain-current')).toContainText('City')
  await expect(page.getByTestId('world-stage')).toBeVisible()
})

test('exports edited 19-cell map, resets then imports it exactly', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Scegli Acqua' }).click()
  await page.getByTestId('terrain-paint').click()
  await expect(page.getByTestId('terrain-current')).toContainText('Acqua')

  await page.getByTestId('map-export').click()
  const json = await page.getByTestId('map-document').inputValue()
  const document = JSON.parse(json) as { schemaVersion: number; radius: number; cells: Array<{ q: number; r: number; terrain: string }> }
  expect(document.schemaVersion).toBe(1)
  expect(document.radius).toBe(2)
  expect(document.cells).toHaveLength(19)
  expect(document.cells.find(cell => cell.q === 0 && cell.r === 0)?.terrain).toBe('water')
  await expect(page.getByTestId('map-document-status')).toContainText('JSON pronto')

  await page.getByTestId('terrain-reset').click()
  await expect(page.getByTestId('terrain-current')).toContainText('Città')
  await page.getByTestId('map-import').click()
  await expect(page.getByTestId('terrain-current')).toContainText('Acqua')
  await expect(page.getByTestId('map-document-status')).toContainText('importata correttamente')
})

test('rejects malformed, secret-bearing and duplicate-cell payloads without changing the map', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('map-export').click()
  const valid = JSON.parse(await page.getByTestId('map-document').inputValue()) as {
    schemaVersion: number; radius: number; cells: Array<{ q: number; r: number; terrain: string }>
  }

  await page.getByTestId('map-document').fill('{broken')
  await page.getByTestId('map-import').click()
  await expect(page.getByTestId('map-document-status')).toContainText('non valido')
  await expect(page.getByTestId('terrain-current')).toContainText('Città')

  const withSecret = { ...valid, playerPositions: [{ playerId: 'bob', q: 0, r: 0 }] }
  await page.getByTestId('map-document').fill(JSON.stringify(withSecret))
  await page.getByTestId('map-import').click()
  await expect(page.getByTestId('map-document-status')).toContainText('non valido')

  const duplicate = { ...valid, cells: [...valid.cells] }
  duplicate.cells[1] = { ...duplicate.cells[0] }
  await page.getByTestId('map-document').fill(JSON.stringify(duplicate))
  await page.getByTestId('map-import').click()
  await expect(page.getByTestId('map-document-status')).toContainText('non valido')
  await expect(page.getByTestId('terrain-current')).toContainText('Città')
})

test('map document editor is translated and usable on mobile without mutating network requests', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const mutations: string[] = []
  page.on('request', request => {
    if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())) mutations.push(request.url())
  })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByTestId('theme-toggle').click()
  await expect(page.getByRole('heading', { name: 'Save and load the map' })).toBeVisible()
  await page.getByRole('button', { name: 'Generate JSON' }).click()
  await expect(page.getByTestId('map-document-status')).toContainText('JSON ready')
  await page.getByRole('button', { name: 'Import JSON' }).click()
  await expect(page.getByTestId('map-document-status')).toContainText('imported successfully')
  expect(mutations).toEqual([])
})

test('serves the original 28-sheet AVIF and matches both sides for the selected hero', async ({ page }) => {
  const response = await page.request.get('/art/hero-ability-cards-original.avif')
  expect(response.ok()).toBeTruthy()
  expect(response.headers()['content-type']).toContain('image/avif')
  expect((await response.body()).length).toBeLessThan(30_000)

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Schede originali di Adsiz' })).toBeVisible()
  const front = page.getByTestId('hero-card-front')
  const back = page.getByTestId('hero-card-back')
  await expect(front).toHaveAttribute('data-hero-id', 'adsiz')
  await expect(back).toHaveAttribute('data-hero-id', 'adsiz')
  await expect(front).toHaveCSS('background-image', /hero-ability-cards-original\.avif/)
  await expect(back).toHaveCSS('background-position', '-640px -375px')

  await page.getByRole('button', { name: 'Seleziona Kira' }).click()
  await expect(page.getByRole('heading', { name: 'Schede originali di Kira' })).toBeVisible()
  await expect(front).toHaveCSS('background-position', '-320px -125px')
  await expect(back).toHaveCSS('background-position', '-960px -500px')
  await expect(page.getByTestId('hero-cards-warning')).toContainText('non sono leggibili')
})

test('original reference sheets follow the hero choice on mobile, in English and dark mode', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByTestId('theme-toggle').click()
  await page.getByRole('button', { name: 'Select Vlko Dlak' }).click()
  await expect(page.getByRole('heading', { name: 'Original sheets for Vlko Dlak' })).toBeVisible()
  await expect(page.getByRole('img', { name: 'Original Front reference sheet for Vlko Dlak' })).toBeVisible()
  await expect(page.getByRole('img', { name: 'Original Reverse reference sheet for Vlko Dlak' })).toBeVisible()
  await expect(page.getByTestId('hero-card-front')).toHaveCSS('background-position', '-320px -375px')
  await expect(page.getByTestId('hero-card-back')).toHaveCSS('background-position', '-960px -750px')
  await expect(page.getByTestId('hero-cards-warning')).toContainText('not legible')
  await expect(page.getByTestId('world-hero')).toContainText('Vlko Dlak')
})

test('serves all original movement-card thumbnails and follows the selected hero', async ({ page }) => {
  const asset = await page.request.get('/art/movement-cards-original.avif')
  expect(asset.ok()).toBeTruthy()
  expect(asset.headers()['content-type']).toContain('image/avif')
  expect((await asset.body()).length).toBeLessThan(30_000)

  await page.goto('/')
  const gallery = page.getByTestId('movement-card-gallery')
  await expect(page.getByRole('heading', { name: 'Carte movimento originali di Adsiz' })).toBeVisible()
  await expect(gallery.getByRole('img')).toHaveCount(10)
  await expect(page.getByTestId('movement-card-1')).toHaveAttribute('data-hero-id', 'adsiz')
  await expect(page.getByTestId('movement-card-10')).toHaveCSS('background-position', '-504px 0px')

  await page.getByRole('button', { name: 'Seleziona Kira' }).click()
  await expect(page.getByRole('heading', { name: 'Carte movimento originali di Kira' })).toBeVisible()
  await expect(gallery.getByRole('img')).toHaveCount(10)
  await expect(page.getByTestId('movement-card-1')).toHaveCSS('background-position', '0px -368px')
  await expect(page.getByTestId('movement-card-10')).toHaveCSS('background-position', '-504px -368px')
  await expect(page.getByTestId('movement-cards-warning')).toContainText('Solo riferimenti grafici')
})

test('Gordunhao source has no movement artwork: never invent replacement cards', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Seleziona Gordunhao' }).click()
  await expect(page.getByTestId('movement-cards-missing')).toContainText('non risultano carte movimento')
  await expect(page.getByTestId('movement-cards-missing')).toContainText('Gordunhao')
  await expect(page.getByTestId('movement-card-gallery')).toHaveCount(0)
  await expect(page.getByTestId('movement-card-1')).toHaveCount(0)
  await page.getByRole('button', { name: 'Seleziona Howler' }).click()
  await expect(page.getByTestId('movement-card-gallery').getByRole('img')).toHaveCount(10)
  await expect(page.getByTestId('movement-card-1')).toHaveCSS('background-position', '0px -184px')
})

test('original movement cards render on English mobile dark mode without network mutations', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const mutations: string[] = []
  page.on('request', request => {
    if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())) mutations.push(request.url())
  })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()
  await page.getByTestId('theme-toggle').click()
  await page.getByRole('button', { name: 'Select Vlko Dlak' }).click()
  await expect(page.getByRole('heading', { name: 'Original movement cards for Vlko Dlak' })).toBeVisible()
  await expect(page.getByRole('img', { name: 'Thumbnail of original movement card 1 for Vlko Dlak' })).toBeVisible()
  await expect(page.getByTestId('movement-card-gallery').getByRole('img')).toHaveCount(10)
  await expect(page.getByTestId('movement-card-10')).toHaveCSS('background-position', '-504px -1104px')
  await expect(page.getByTestId('movement-cards-warning')).toContainText('Artwork references only')
  expect(mutations).toEqual([])
})

