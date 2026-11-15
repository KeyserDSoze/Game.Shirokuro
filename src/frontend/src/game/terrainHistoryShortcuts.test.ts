import { describe, expect, it } from 'vitest'
import {
  classifyTerrainHistoryShortcut, TERRAIN_HISTORY_EDITABLE_SELECTOR,
  type TerrainHistoryShortcutAction, type TerrainHistoryShortcutInput,
} from './terrainHistoryShortcuts'

const base: TerrainHistoryShortcutInput = Object.freeze({
  key: 'z',
  ctrlKey: false,
  metaKey: false,
  shiftKey: false,
  altKey: false,
  repeat: false,
  isComposing: false,
  defaultPrevented: false,
  editableTarget: false,
})

const cases: {
  label: string
  flags: Partial<TerrainHistoryShortcutInput>
  expected: TerrainHistoryShortcutAction
}[] = [
  { label: 'Ctrl+Z', flags: { ctrlKey: true }, expected: 'undo' },
  { label: 'Cmd+Z', flags: { metaKey: true }, expected: 'undo' },
  { label: 'uppercase Ctrl+Z', flags: { ctrlKey: true, key: 'Z' }, expected: 'undo' },
  { label: 'Ctrl+Shift+Z', flags: { ctrlKey: true, shiftKey: true }, expected: 'redo' },
  { label: 'Cmd+Shift+Z', flags: { metaKey: true, shiftKey: true }, expected: 'redo' },
  { label: 'Ctrl+Y', flags: { ctrlKey: true, key: 'y' }, expected: 'redo' },
  { label: 'Cmd+Y', flags: { metaKey: true, key: 'y' }, expected: 'redo' },
  { label: 'ordinary Z', flags: {}, expected: null },
  { label: 'Shift+Z', flags: { shiftKey: true }, expected: null },
  { label: 'ordinary Y', flags: { key: 'y' }, expected: null },
  { label: 'unrelated command', flags: { key: 'x', ctrlKey: true }, expected: null },
  { label: 'Ctrl+Shift+Y', flags: { ctrlKey: true, key: 'y', shiftKey: true }, expected: null },
  { label: 'Ctrl+Meta+Z', flags: { ctrlKey: true, metaKey: true }, expected: null },
  { label: 'Alt+Ctrl+Z', flags: { ctrlKey: true, altKey: true }, expected: null },
  { label: 'repeat Ctrl+Z', flags: { ctrlKey: true, repeat: true }, expected: null },
  { label: 'IME Ctrl+Z', flags: { ctrlKey: true, isComposing: true }, expected: null },
  { label: 'prevented Ctrl+Z', flags: { ctrlKey: true, defaultPrevented: true }, expected: null },
  { label: 'text field Ctrl+Z', flags: { ctrlKey: true, editableTarget: true }, expected: null },
  {
    label: 'text field Ctrl+Shift+Z',
    flags: { ctrlKey: true, shiftKey: true, editableTarget: true },
    expected: null,
  },
  {
    label: 'text field Cmd+Y',
    flags: { metaKey: true, key: 'y', editableTarget: true },
    expected: null,
  },
]

describe('visual-only keyboard terrain history shortcuts', () => {
  it.each(cases)('$label', ({ flags, expected }) => {
    const input = Object.freeze({ ...base, ...flags })
    expect(classifyTerrainHistoryShortcut(input)).toBe(expected)
    // Classification is pure and cannot modify native browser key state.
    expect(base.ctrlKey).toBe(false)
    expect(input).toEqual({ ...base, ...flags })
  })

  it('excludes all editor text surfaces including nested contenteditable and ARIA text widgets', () => {
    for (const name of ['input', 'textarea', 'select', '[contenteditable]',
      '[role="textbox"]', '[role="searchbox"]']) {
      expect(TERRAIN_HISTORY_EDITABLE_SELECTOR).toContain(name)
    }
  })
})
