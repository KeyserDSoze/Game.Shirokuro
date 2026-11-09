import { describe, expect, it } from 'vitest'
import { axialToWorld, createHexBoard, hexDistance } from './hex'

describe('axial hex geometry', () => {
  it.each([[0, 1], [1, 7], [2, 19], [3, 37]])('radius %i creates %i unique tiles', (radius, count) => {
    const tiles = createHexBoard(radius)
    expect(tiles).toHaveLength(count)
    expect(new Set(tiles.map(({ q, r }) => q + ':' + r)).size).toBe(count)
    expect(tiles.every((hex) => hexDistance(hex, { q: 0, r: 0 }) <= radius)).toBe(true)
  })

  it.each([
    [{ q: 0, r: 0 }, { q: 0, r: 0 }, 0],
    [{ q: 0, r: 0 }, { q: 1, r: 0 }, 1],
    [{ q: 2, r: -1 }, { q: -1, r: 1 }, 3],
  ])('distance is symmetric for %o and %o', (first, second, expected) => {
    expect(hexDistance(first, second)).toBe(expected)
    expect(hexDistance(second, first)).toBe(expected)
  })

  it.each([-1, 0.5, 7, Number.NaN])('rejects invalid radius %s', (radius) => {
    expect(() => createHexBoard(radius)).toThrow(RangeError)
  })

  it('maps origin to world origin and offsets neighbors', () => {
    expect(axialToWorld({ q: 0, r: 0 })).toEqual({ x: 0, z: 0 })
    expect(axialToWorld({ q: 0, r: 1 }).z).toBe(1.5)
  })
})

