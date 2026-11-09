import { createHexBoard, type HexCoordinate } from './hex'
import {
  defaultPreviewTerrain, terrainAtCell, terrainHexKey, terrainIds,
  type TerrainId, type TerrainOverrides,
} from './terrain'

/**
 * Format for sharing *visual-only* hex previews.
 * Intentionally excludes hero positions, routes, spawn locations, treasure,
 * fog-of-war and every authoritative gameplay rule.
 */
export const PREVIEW_MAP_SCHEMA_VERSION = 1
export const PREVIEW_MAP_RADIUS = 2
export const MAX_PREVIEW_MAP_JSON_LENGTH = 8192

const board = createHexBoard(PREVIEW_MAP_RADIUS)
const validKeys = new Set(board.map(terrainHexKey))
const knownTerrains: ReadonlySet<string> = new Set(terrainIds)

export type PreviewMapCell = Readonly<HexCoordinate & { terrain: TerrainId }>
export type PreviewMapDocument = Readonly<{
  schemaVersion: 1
  radius: 2
  cells: readonly PreviewMapCell[]
}>

export class PreviewMapValidationError extends Error {
  constructor() {
    super('Invalid or unsupported local preview map JSON.')
    this.name = 'PreviewMapValidationError'
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function hasExactlyKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Object.keys(value)
  return keys.length === expected.length && keys.every(key => expected.includes(key))
}

export function exportPreviewMap(overrides: TerrainOverrides): string {
  const document: PreviewMapDocument = {
    schemaVersion: PREVIEW_MAP_SCHEMA_VERSION,
    radius: PREVIEW_MAP_RADIUS,
    cells: board.map(hex => ({
      q: hex.q,
      r: hex.r,
      terrain: terrainAtCell(hex, overrides),
    })),
  }
  return JSON.stringify(document, null, 2)
}

/** Strict, atomic import: do not expose raw untrusted JSON to UI/game state. */
export function importPreviewMap(json: string): TerrainOverrides {
  if (typeof json !== 'string' || json.length === 0 ||
      json.length > MAX_PREVIEW_MAP_JSON_LENGTH) {
    throw new PreviewMapValidationError()
  }

  let parsed: unknown
  try {
    parsed = JSON.parse(json) as unknown
  } catch {
    throw new PreviewMapValidationError()
  }
  if (!isRecord(parsed) || !hasExactlyKeys(parsed, ['schemaVersion', 'radius', 'cells']) ||
      parsed.schemaVersion !== PREVIEW_MAP_SCHEMA_VERSION ||
      parsed.radius !== PREVIEW_MAP_RADIUS ||
      !Array.isArray(parsed.cells) || parsed.cells.length !== board.length) {
    throw new PreviewMapValidationError()
  }

  const seen = new Set<string>()
  const imported: Record<string, TerrainId> = Object.create(null) as Record<string, TerrainId>
  for (const cell of parsed.cells as unknown[]) {
    if (!isRecord(cell) || !hasExactlyKeys(cell, ['q', 'r', 'terrain']) ||
        !Number.isInteger(cell.q) || !Number.isInteger(cell.r) ||
        typeof cell.terrain !== 'string' || !knownTerrains.has(cell.terrain)) {
      throw new PreviewMapValidationError()
    }

    const hex: HexCoordinate = { q: cell.q as number, r: cell.r as number }
    const key = terrainHexKey(hex)
    if (!validKeys.has(key) || seen.has(key)) {
      throw new PreviewMapValidationError()
    }

    seen.add(key)
    const terrain = cell.terrain as TerrainId
    if (terrain !== defaultPreviewTerrain(hex)) {
      imported[key] = terrain
    }
  }

  if (seen.size !== validKeys.size) {
    throw new PreviewMapValidationError()
  }
  return imported
}

