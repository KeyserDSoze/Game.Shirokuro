import { createHexBoard } from './hex'
import { PREVIEW_MAP_RADIUS } from './mapDocument'
import { terrainAtCell, type TerrainOverrides } from './terrain'

/**
 * Local visual editor history; never a gameplay log or authoritative state.
 * Immutable snapshots and a fixed cap keep even repeated edits bounded.
 */
export const MAX_TERRAIN_HISTORY_STEPS = 32

export type TerrainEditHistory = Readonly<{
  past: readonly TerrainOverrides[]
  present: TerrainOverrides
  future: readonly TerrainOverrides[]
}>

const cells = createHexBoard(PREVIEW_MAP_RADIUS)

function snapshot(overrides: TerrainOverrides): TerrainOverrides {
  return { ...overrides }
}

function sameEffectiveMap(a: TerrainOverrides, b: TerrainOverrides): boolean {
  return cells.every(cell => terrainAtCell(cell, a) === terrainAtCell(cell, b))
}

export function createTerrainHistory(initial: TerrainOverrides = {}): TerrainEditHistory {
  return { past: [], present: snapshot(initial), future: [] }
}

/** One atomic edit, including full reset or validated JSON import. */
export function commitTerrainEdit(
  current: TerrainEditHistory, next: TerrainOverrides,
): TerrainEditHistory {
  if (sameEffectiveMap(current.present, next)) return current

  return {
    past: [...current.past, snapshot(current.present)].slice(-MAX_TERRAIN_HISTORY_STEPS),
    present: snapshot(next),
    future: [],
  }
}

export function undoTerrainEdit(current: TerrainEditHistory): TerrainEditHistory {
  if (current.past.length === 0) return current
  const previous = current.past[current.past.length - 1]
  return {
    past: current.past.slice(0, -1),
    present: snapshot(previous),
    future: [snapshot(current.present), ...current.future].slice(0, MAX_TERRAIN_HISTORY_STEPS),
  }
}

export function redoTerrainEdit(current: TerrainEditHistory): TerrainEditHistory {
  if (current.future.length === 0) return current
  const next = current.future[0]
  return {
    past: [...current.past, snapshot(current.present)].slice(-MAX_TERRAIN_HISTORY_STEPS),
    present: snapshot(next),
    future: current.future.slice(1),
  }
}
