import type { HexCoordinate } from './hex'

/**
 * These six visual categories come from the user's original tasselli PNG files.
 * This module is preview-only: NO movement costs, visibility rules or map data.
 */
export const terrainIds = ['black', 'white', 'city', 'sand', 'water', 'wild'] as const
export type TerrainId = (typeof terrainIds)[number]
export type TerrainOverrides = Readonly<Record<string, TerrainId>>
export const TERRAIN_ATLAS_COLUMNS = 3
export const TERRAIN_ATLAS_ROWS = 2
export const TERRAIN_ATLAS_CELL_SIZE = 64
export const TERRAIN_ATLAS_PATH = 'art/terrain-original-atlas.webp'

export function terrainAtlasSlot(terrain: TerrainId) {
  const index: number = terrainIds.indexOf(terrain)
  if (index < 0) throw new RangeError('Unrecognized preview terrain')
  return { index, column: index % TERRAIN_ATLAS_COLUMNS, row: Math.floor(index / TERRAIN_ATLAS_COLUMNS) }
}

export function terrainHexKey({ q, r }: HexCoordinate) {
  return `${q}:${r}`
}

/** Deterministic *illustrative* distribution, unrelated to the final official map. */
export function defaultPreviewTerrain({ q, r }: HexCoordinate): TerrainId {
  const index = (((q * 7 + r * 5 + 2) % terrainIds.length) + terrainIds.length) % terrainIds.length
  return terrainIds[index]
}

export function terrainAtCell(hex: HexCoordinate, overrides: TerrainOverrides): TerrainId {
  return overrides[terrainHexKey(hex)] ?? defaultPreviewTerrain(hex)
}

export function paintTerrainCell(
  overrides: TerrainOverrides,
  hex: HexCoordinate,
  selectedTerrain: TerrainId,
): TerrainOverrides {
  terrainAtlasSlot(selectedTerrain)
  const key = terrainHexKey(hex)
  if (overrides[key] === selectedTerrain) return overrides
  return { ...overrides, [key]: selectedTerrain }
}

