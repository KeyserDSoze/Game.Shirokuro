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

