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

