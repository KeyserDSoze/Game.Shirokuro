import { describe, expect, it } from 'vitest'
import { createHexBoard } from './hex'
import { terrainAtCell, paintTerrainCell } from './terrain'
import {
  exportPreviewMap, importPreviewMap, MAX_PREVIEW_MAP_JSON_LENGTH,
  PREVIEW_MAP_RADIUS, PREVIEW_MAP_SCHEMA_VERSION, PreviewMapValidationError,
} from './mapDocument'

type DraftDocument = { schemaVersion: number; radius: number; cells: Array<{ q: number; r: number; terrain: string }> }

function draft(): DraftDocument {
  return JSON.parse(exportPreviewMap({})) as DraftDocument
}

function reject(document: unknown) {
  expect(() => importPreviewMap(JSON.stringify(document))).toThrow(PreviewMapValidationError)
}

describe('versioned, non-authoritative hex map interchange', () => {
  it('exports a complete canonical board with exactly 19 hexes', () => {
    const data = draft()
    expect(data.schemaVersion).toBe(PREVIEW_MAP_SCHEMA_VERSION)
    expect(data.radius).toBe(PREVIEW_MAP_RADIUS)
    expect(data.cells).toHaveLength(19)
    expect(data.cells.map(cell => `${cell.q}:${cell.r}`)).toEqual(
      createHexBoard(2).map(cell => `${cell.q}:${cell.r}`),
    )
  })

  it('roundtrips a painted center and two distant cells without changing source', () => {
    const original = {}
    const painted = paintTerrainCell(
      paintTerrainCell(
        paintTerrainCell(original, { q: 0, r: 0 }, 'water'),
        { q: -2, r: 2 }, 'black',
      ),
      { q: 2, r: -2 }, 'sand',
    )
    const restored = importPreviewMap(exportPreviewMap(painted))
    for (const hex of createHexBoard(2)) {
      expect(terrainAtCell(hex, restored)).toBe(terrainAtCell(hex, painted))
    }
    expect(original).toEqual({})
    expect(terrainAtCell({ q: 0, r: 0 }, restored)).toBe('water')
  })

  it('is deterministic and ignores override insertion order and redundant overrides', () => {
    const first = { '0:0': 'water' as const, '-2:2': 'black' as const }
    const second = { '-2:2': 'black' as const, '0:0': 'water' as const }
    expect(exportPreviewMap(first)).toBe(exportPreviewMap(second))
    expect(exportPreviewMap({ '0:0': 'city' })).toBe(exportPreviewMap({}))
  })

  it('normalizes a noncanonical valid import and excludes unchanged defaults', () => {
    const data = draft()
    data.cells.reverse()
    const restored = importPreviewMap(JSON.stringify(data))
    expect(Object.keys(restored)).toHaveLength(0)
    expect(exportPreviewMap(restored)).toBe(exportPreviewMap({}))
  })

  it('does not create dangerous/prototype properties when parsing JSON', () => {
    const data = draft()
    const injected = JSON.stringify(data).replace('"radius":2', '"__proto__":{"hacked":true},"radius":2')
    expect(() => importPreviewMap(injected)).toThrow(PreviewMapValidationError)
    expect(({} as Record<string, unknown>).hacked).toBeUndefined()
  })

  it.each([
    ['invalid syntax', '{'],
    ['empty input', ''],
    ['non-object', '[]'],
    ['null', 'null'],
    ['oversized', ' '.repeat(MAX_PREVIEW_MAP_JSON_LENGTH + 1)],
  ])('rejects %s', (_label, json) => {
    expect(() => importPreviewMap(json)).toThrow(PreviewMapValidationError)
  })

  it('rejects wrong versions/radii and extra root fields', () => {
    reject({ ...draft(), schemaVersion: 2 })
    reject({ ...draft(), radius: 3 })
    reject({ ...draft(), radius: '2' })
    reject({ ...draft(), playerPositions: [] })
  })

  it('rejects missing, duplicated and out-of-board cells', () => {
    const missing = draft()
    missing.cells.pop()
    reject(missing)

    const duplicate = draft()
    duplicate.cells[1] = { ...duplicate.cells[0] }
    reject(duplicate)

    const outOfBounds = draft()
    outOfBounds.cells[0].q = 50
    reject(outOfBounds)
  })

  it('rejects invalid coordinates and terrain values', () => {
    const fractional = draft()
    fractional.cells[0].q = 0.5
    reject(fractional)

    const invalidTerrain = draft()
    invalidTerrain.cells[0].terrain = 'fire'
    reject(invalidTerrain)

    const invalidCoordinate = draft()
    invalidCoordinate.cells[0].r = Number.MAX_SAFE_INTEGER
    reject(invalidCoordinate)

    const extraField = draft()
    reject({ ...extraField, cells: [{ ...extraField.cells[0], plannedDestination: [1, 1] }, ...extraField.cells.slice(1)] })
  })

  it('does not retain a reference to the parsed input or add game secrets', () => {
    const input = draft()
    input.cells[0].terrain = 'water'
    const saved = importPreviewMap(JSON.stringify(input))
    input.cells[0].terrain = 'black'
    expect(Object.keys(saved).every(key => /^-?\d+:-?\d+$/.test(key))).toBe(true)
    expect(JSON.stringify(saved)).not.toMatch(/playerId|treasure|destination|route/)
  })
})

