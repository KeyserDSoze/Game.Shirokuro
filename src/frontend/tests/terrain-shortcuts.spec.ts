import { expect, test } from '@playwright/test'

test('preview terrain history shortcuts work without capturing JSON editor undo or writing to API', async ({ page }) => {
  await page.goto('/')

  const mutations: string[] = []
  page.on('request', request => {
    if (['POST', 'PATCH', 'DELETE'].includes(request.method()) &&
        new URL(request.url()).pathname.startsWith('/api/')) {
      mutations.push(request.method())
    }
  })

  const terrain = page.getByTestId('terrain-current')
  const original = (await terrain.textContent())!.trim()
  const undo = page.getByTestId('terrain-undo')
  const redo = page.getByTestId('terrain-redo')
  await expect(undo).toBeDisabled()
  await expect(redo).toBeDisabled()

  await page.getByTestId('terrain-brush-water').click()
  await page.getByTestId('terrain-paint').click()
  const painted = (await terrain.textContent())!.trim()
  expect(painted).not.toBe(original)
  await expect(undo).toBeEnabled()
  await expect(redo).toBeDisabled()

  // The standard desktop shortcuts operate only on local map history.
  await page.keyboard.press('Control+z')
  await expect(terrain).toHaveText(original)
  await expect(redo).toBeEnabled()

  await page.keyboard.press('Control+Shift+z')
  await expect(terrain).toHaveText(painted)
  await expect(redo).toBeDisabled()

  await page.keyboard.press('Control+z')
  await expect(terrain).toHaveText(original)
  await page.keyboard.press('Control+y')
  await expect(terrain).toHaveText(painted)

  // The JSON textarea retains native browser Undo, not terrain-map Undo.
  const documentEditor = page.getByTestId('map-document')
  await documentEditor.fill('draft text')
  await documentEditor.press('Control+z')
  await expect(terrain).toHaveText(painted)
  await expect(undo).toBeEnabled()
  await expect(redo).toBeDisabled()

  // Same restriction for descendants of a contenteditable root.
  await page.evaluate(() => {
    const editable = document.createElement('div')
    editable.contentEditable = 'true'
    editable.dataset.testid = 'nested-terrain-editable'
    editable.innerHTML = '<span>Draft</span>'
    document.body.appendChild(editable)
  })
  await page.getByTestId('nested-terrain-editable').focus()
  await page.keyboard.press('Control+z')
  await expect(terrain).toHaveText(painted)

  // Leaving the text editor restores keyboard controls.
  await page.getByTestId('terrain-paint').focus()
  await page.keyboard.press('Control+z')
  await expect(terrain).toHaveText(original)
  await expect(redo).toBeEnabled()
  expect(mutations).toEqual([])
})
