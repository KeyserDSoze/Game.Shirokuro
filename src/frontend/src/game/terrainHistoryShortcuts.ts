/**
 * Local preview terrain-editor shortcuts only. Keep this decision separate
 * from React and DOM so keyboard combinations and exclusions are testable.
 * The caller handles actual target ancestry and only consumes an event when
 * its corresponding history operation is available.
 */
export type TerrainHistoryShortcutAction = 'undo' | 'redo' | null

export type TerrainHistoryShortcutInput = Readonly<{
  key: string
  ctrlKey: boolean
  metaKey: boolean
  shiftKey: boolean
  altKey: boolean
  repeat: boolean
  isComposing: boolean
  defaultPrevented: boolean
  editableTarget: boolean
}>

/**
 * Be conservative about browser-native editing, including descendants of
 * an editable element or an ARIA text widget. No gameplay keyboard binding.
 */
export const TERRAIN_HISTORY_EDITABLE_SELECTOR =
  'input, textarea, select, [contenteditable], [role="textbox"], [role="searchbox"]'

export function classifyTerrainHistoryShortcut(
  input: TerrainHistoryShortcutInput,
): TerrainHistoryShortcutAction {
  // Skip native text editing, composed characters, held-key repeats and
  // already-handled shortcuts. Ctrl+Meta combinations are intentionally
  // ignored rather than claiming a nonstandard browser command.
  if (input.editableTarget || input.defaultPrevented || input.isComposing ||
      input.repeat || input.altKey || input.ctrlKey === input.metaKey) {
    return null
  }

  switch (input.key.toLowerCase()) {
    case 'z':
      return input.shiftKey ? 'redo' : 'undo'
    case 'y':
      return input.shiftKey ? null : 'redo'
    default:
      return null
  }
}
