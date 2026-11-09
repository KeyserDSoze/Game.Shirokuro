import { expect, test } from '@playwright/test'

test('shows the technical game shell without claiming an active match', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: /shirokuro/i })).toBeVisible()
  await expect(page.getByTestId('world-stage')).toBeVisible()
  await expect(page.getByTestId('selected-hex')).toContainText('Q')
  await expect(page.getByText('nessuna partita attiva', { exact: false })).toBeVisible()
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

