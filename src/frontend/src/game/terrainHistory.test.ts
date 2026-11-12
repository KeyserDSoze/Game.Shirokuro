import { describe, expect, it } from 'vitest'
import { exportPreviewMap, importPreviewMap } from './mapDocument'
import { paintTerrainCell, terrainAtCell, type TerrainId, type TerrainOverrides } from './terrain'
import {
  MAX_TERRAIN_HISTORY_STEPS,
  commitTerrainEdit, createTerrainHistory, redoTerrainEdit, undoTerrainEdit,
} from './terrainHistory'

const center = { q: 0, r: 0 }
const terrain = (value: TerrainOverrides) => terrainAtCell(center, value)

describe('bounded, visual-only terrain edit history', () => {
  it('starts empty and cannot undo or redo an empty timeline', () => {
    const history = createTerrainHistory()
    expect(terrain(history.present)).toBe('city')
    expect(undoTerrainEdit(history)).toBe(history)
    expect(redoTerrainEdit(history)).toBe(history)
    expect(history.past).toEqual([])
    expect(history.future).toEqual([])
  })

  it('undoes and redoes paints without affecting the original snapshots', () => {
    const empty = createTerrainHistory()
    const painted = commitTerrainEdit(
      empty, paintTerrainCell(empty.present, center, 'water'))
    const other = commitTerrainEdit(
      painted, paintTerrainCell(painted.present, center, 'black'))
    expect(terrain(empty.present)).toBe('city')
    expect(terrain(painted.present)).toBe('water')
    expect(terrain(other.present)).toBe('black')

    const undone = undoTerrainEdit(other)
    expect(terrain(undone.present)).toBe('water')
    expect(terrain(undoTerrainEdit(undone).present)).toBe('city')
    expect(terrain(redoTerrainEdit(undone).present)).toBe('black')
    expect(redoTerrainEdit(other)).toBe(other)
  })

  it('discards redo only on a real new edit, not on a no-op', () => {
    const first = createTerrainHistory()
    const water = commitTerrainEdit(
      first, paintTerrainCell(first.present, center, 'water'))
    const undone = undoTerrainEdit(water)
    expect(undone.future).toHaveLength(1)
    // City is already the effective default terrain at the center.
    const unchanged = commitTerrainEdit(
      undone, paintTerrainCell(undone.present, center, 'city'))
    expect(unchanged).toBe(undone)
    expect(unchanged.future).toHaveLength(1)

    const black = commitTerrainEdit(
      unchanged, paintTerrainCell(unchanged.present, center, 'black'))
    expect(black.future).toHaveLength(0)
    expect(redoTerrainEdit(black)).toBe(black)
    expect(terrain(undoTerrainEdit(black).present)).toBe('city')
  })

  it('reset and import are atomic undoable steps and identical imports are no-ops', () => {
    const first = createTerrainHistory()
    const water = commitTerrainEdit(
      first, paintTerrainCell(first.present, center, 'water'))
    const json = exportPreviewMap(water.present)
    const reset = commitTerrainEdit(water, {})
    expect(terrain(reset.present)).toBe('city')
    expect(terrain(undoTerrainEdit(reset).present)).toBe('water')

    const imported = commitTerrainEdit(reset, importPreviewMap(json))
    expect(terrain(imported.present)).toBe('water')
    expect(terrain(undoTerrainEdit(imported).present)).toBe('city')
    expect(terrain(redoTerrainEdit(undoTerrainEdit(imported)).present)).toBe('water')
    expect(commitTerrainEdit(imported, importPreviewMap(json))).toBe(imported)
  })

  it('does not change editor history when import parsing fails', () => {
    const current = createTerrainHistory()
    expect(() => commitTerrainEdit(current, importPreviewMap('{"invalid":true}'))).toThrow()
    expect(current).toEqual(createTerrainHistory())
  })

  it('copies imported snapshots rather than retaining mutable caller references', () => {
    const source = { '0:0': 'water' as TerrainId }
    const history = createTerrainHistory(source)
    source['0:0'] = 'black'
    expect(terrain(history.present)).toBe('water')

    const next = { '0:0': 'sand' as TerrainId }
    const edited = commitTerrainEdit(history, next)
    next['0:0'] = 'city'
    expect(terrain(edited.present)).toBe('sand')
    expect(terrain(history.present)).toBe('water')
    expect(terrain(undoTerrainEdit(edited).present)).toBe('water')
  })

  it('retains at most 32 real changes, with bounded undo and redo', () => {
    let history = createTerrainHistory()
    const changes: readonly TerrainId[] = ['water', 'black', 'sand', 'wild', 'city', 'white']
    for (let i = 0; i < 40; i++) {
      history = commitTerrainEdit(history, paintTerrainCell(
        history.present, center, changes[i % changes.length]))
      expect(history.past.length).toBeLessThanOrEqual(MAX_TERRAIN_HISTORY_STEPS)
      expect(history.future).toHaveLength(0)
    }
    expect(history.past).toHaveLength(MAX_TERRAIN_HISTORY_STEPS)
    const end = history
    for (let i = 0; i < MAX_TERRAIN_HISTORY_STEPS; i++) {
      history = undoTerrainEdit(history)
    }
    expect(history.past).toHaveLength(0)
    expect(history.future).toHaveLength(MAX_TERRAIN_HISTORY_STEPS)
    expect(undoTerrainEdit(history)).toBe(history)
    expect(terrain(history.present)).toBe(changes[7 % changes.length])
    for (let i = 0; i < MAX_TERRAIN_HISTORY_STEPS; i++) {
      history = redoTerrainEdit(history)
    }
    expect(history.future).toHaveLength(0)
    expect(redoTerrainEdit(history)).toBe(history)
    expect(terrain(history.present)).toBe(terrain(end.present))
  })
})
