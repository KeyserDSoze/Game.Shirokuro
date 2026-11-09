import { describe, expect, it } from 'vitest'
import { createHexBoard } from './hex'
import {
  defaultPreviewTerrain, paintTerrainCell, terrainAtCell, terrainAtlasSlot,
  terrainHexKey, terrainIds, TERRAIN_ATLAS_COLUMNS, TERRAIN_ATLAS_ROWS,
} from './terrain'

describe('local terrain preview, not gameplay', () => {
  it('maps exactly six original categories to unique 3x2 atlas cells', () => {
    expect(terrainIds).toHaveLength(TERRAIN_ATLAS_COLUMNS * TERRAIN_ATLAS_ROWS)
    const slots = terrainIds.map(terrainAtlasSlot)
    expect(new Set(slots.map(slot => `${slot.column}:${slot.row}`)).size).toBe(6)
    expect(slots.map(slot => slot.index)).toEqual([0, 1, 2, 3, 4, 5])
  })

  it.each([
    ['black', 0, 0], ['white', 1, 0], ['city', 2, 0],
    ['sand', 0, 1], ['water', 1, 1], ['wild', 2, 1],
  ] as const)('maps %s to atlas %i:%i', (terrain, column, row) => {
    expect(terrainAtlasSlot(terrain)).toMatchObject({ column, row })
  })

  it('contains each terrain at least once in the illustrative radius-two scene', () => {
    const kinds = new Set(createHexBoard(2).map(defaultPreviewTerrain))
    expect(kinds).toEqual(new Set(terrainIds))
  })

  it('uses the city art as an arbitrary illustrative center default', () => {
    expect(defaultPreviewTerrain({ q: 0, r: 0 })).toBe('city')
  })

  it('is deterministic, including negative axial coordinates', () => {
    const a = { q: -2, r: 1 }
    expect(terrainHexKey(a)).toBe('-2:1')
    expect(defaultPreviewTerrain(a)).toBe(defaultPreviewTerrain({ ...a }))
    expect(terrainIds).toContain(defaultPreviewTerrain(a))
  })

  it('painting changes only the given tile, preserving the original snapshot', () => {
    const center = { q: 0, r: 0 }
    const neighbor = { q: 1, r: 0 }
    const before = {}
    const after = paintTerrainCell(before, center, 'water')
    expect(terrainAtCell(center, before)).toBe('city')
    expect(terrainAtCell(center, after)).toBe('water')
    expect(terrainAtCell(neighbor, after)).toBe(defaultPreviewTerrain(neighbor))
    expect(before).toEqual({})
  })

  it('repeated painting of the same cell is idempotent', () => {
    const hex = { q: 0, r: 0 }
    const painted = paintTerrainCell({}, hex, 'wild')
    expect(paintTerrainCell(painted, hex, 'wild')).toBe(painted)
  })

  it('does not mutate an existing map and reset restores the demonstration pattern', () => {
    const map = Object.freeze({ '0:0': 'black' as const })
    const updated = paintTerrainCell(map, { q: 1, r: 0 }, 'sand')
    expect(updated['0:0']).toBe('black')
    expect(map).toEqual({ '0:0': 'black' })
    expect(terrainAtCell({ q: 0, r: 0 }, {})).toBe('city')
  })

  it('rejects bogus terrain IDs from untrusted runtime data', () => {
    expect(() => terrainAtlasSlot('unknown' as never)).toThrow(RangeError)
    expect(() => paintTerrainCell({}, { q: 0, r: 0 }, 'javascript:' as never)).toThrow(RangeError)
  })
})

