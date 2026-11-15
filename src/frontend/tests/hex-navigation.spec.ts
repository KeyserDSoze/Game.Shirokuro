import { expect, test } from '@playwright/test'

test('six accessible axial controls select and paint a preview cell without any API mutation', async ({ page }) => {
  const mutations: string[] = []
  page.on('request', request => {
    if (['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method()) &&
        new URL(request.url()).pathname.startsWith('/api/')) {
      mutations.push(request.url())
    }
  })

  await page.goto('/')
  const navigation = page.getByTestId('hex-navigation')
  await expect(navigation).toHaveAttribute('role', 'group')
  await expect(navigation).toHaveAttribute('aria-label', 'Esplora gli esagoni con la tastiera')
  await expect(navigation.getByRole('button')).toHaveCount(6)
  await expect(page.getByTestId('selected-hex')).toHaveAttribute('role', 'status')
  await expect(page.getByTestId('selected-q')).toHaveText('0')
  await expect(page.getByTestId('selected-r')).toHaveText('0')

  const east = page.getByTestId('hex-nav-east')
  await east.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('selected-q')).toHaveText('1')
  await expect(page.getByTestId('selected-r')).toHaveText('0')

  await east.focus()
  await page.keyboard.press('Space')
  await expect(page.getByTestId('selected-q')).toHaveText('2')
  await expect(page.getByTestId('selected-r')).toHaveText('0')
  await expect(east).toBeDisabled()
  await expect(page.getByTestId('hex-nav-southEast')).toBeDisabled()

  // Keyboard selection uses the very same terrain editor as pointer selection.
  await page.getByTestId('terrain-brush-black').focus()
  await page.keyboard.press('Enter')
  await page.getByTestId('terrain-paint').focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('terrain-current')).toContainText('Nero')

  await page.getByTestId('map-export').click()
  const json = JSON.parse(await page.getByTestId('map-document').inputValue()) as {
    cells: Array<{ q: number; r: number; terrain: string }>
  }
  expect(json.cells.find(cell => cell.q === 2 && cell.r === 0)?.terrain).toBe('black')
  expect(json.cells.find(cell => cell.q === 0 && cell.r === 0)?.terrain).toBe('city')

  // Selection is never a server movement, match action or mutation.
  await page.getByTestId('hex-nav-west').click()
  await page.getByTestId('hex-nav-west').click()
  await expect(page.getByTestId('selected-q')).toHaveText('0')
  await expect(page.getByTestId('selected-r')).toHaveText('0')
  await expect(page.getByTestId('terrain-current')).toContainText('Città')
  expect(mutations).toEqual([])
})

test('mobile English hex controls expose clear labels and prevent leaving the board', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('language-toggle').click()

  const navigation = page.getByRole('group', { name: 'Explore hexes by keyboard' })
  await expect(navigation.getByRole('button')).toHaveCount(6)
  await expect(page.getByText('Only changes the preview selection:', { exact: false })).toBeVisible()
  const northEast = page.getByTestId('hex-nav-northEast')
  await expect(northEast).toHaveAccessibleName('North-east')
  await northEast.focus()
  await page.keyboard.press('Space')
  await expect(page.getByTestId('selected-q')).toHaveText('1')
  await expect(page.getByTestId('selected-r')).toHaveText('-1')
  await northEast.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('selected-q')).toHaveText('2')
  await expect(page.getByTestId('selected-r')).toHaveText('-2')
  await expect(northEast).toBeDisabled()
  await expect(page.getByTestId('hex-nav-east')).toBeDisabled()
  await expect(page.getByTestId('world-stage')).toBeVisible()
})
