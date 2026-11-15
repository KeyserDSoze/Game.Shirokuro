import { describe, expect, it } from 'vitest'
import { createHexBoard, hexDistance } from './hex'
import {
  isPreviewHexCoordinate, previewHexDirections, stepPreviewHex,
  type PreviewHexDirection,
} from './hexNavigation'

const center = { q: 0, r: 0 }
const key = (cell: { q: number; r: number }) => `${cell.q}:${cell.r}`

describe('accessible visual-only hex navigation', () => {
  it('offers exactly six distinct adjacent directions from the center', () => {
    expect(previewHexDirections).toHaveLength(6)
    expect(new Set(previewHexDirections.map(direction => direction.id)).size).toBe(6)
    const neighbors = previewHexDirections.map(direction =>
      stepPreviewHex(center, direction.id))
    expect(neighbors).toEqual([
      { q: 1, r: 0 },
      { q: 1, r: -1 },
      { q: 0, r: -1 },
      { q: -1, r: 0 },
      { q: -1, r: 1 },
      { q: 0, r: 1 },
    ])
    expect(neighbors.every(cell => cell !== null &&
      hexDistance(center, cell) === 1)).toBe(true)
  })

  it('rejects steps beyond all six board edges and preserves the input', () => {
    const edges = [
      { from: { q: 2, r: 0 }, direction: 'east' },
      { from: { q: 2, r: -2 }, direction: 'northEast' },
      { from: { q: 0, r: -2 }, direction: 'northWest' },
      { from: { q: -2, r: 0 }, direction: 'west' },
      { from: { q: -2, r: 2 }, direction: 'southWest' },
      { from: { q: 0, r: 2 }, direction: 'southEast' },
    ] as const
    for (const { from, direction } of edges) {
      const before = { ...from }
      expect(stepPreviewHex(from, direction)).toBeNull()
      expect(from).toEqual(before)
    }
  })

  it('never accepts non-integer or off-board selected coordinates', () => {
    for (const selected of [
      { q: 0.5, r: 0 }, { q: 3, r: 0 },
      { q: Number.NaN, r: 0 }, { q: Number.POSITIVE_INFINITY, r: 0 },
      { q: 0, r: Number.NEGATIVE_INFINITY },
    ]) {
      expect(isPreviewHexCoordinate(selected)).toBe(false)
      for (const { id } of previewHexDirections) {
        expect(stepPreviewHex(selected, id)).toBeNull()
      }
    }
    expect(stepPreviewHex(center, 'invalid' as PreviewHexDirection)).toBeNull()
  })

  it('reaches every one of the 19 local preview cells with valid steps', () => {
    const board = createHexBoard(2)
    expect(board).toHaveLength(19)
    const known = new Set(board.map(key))
    const visited = new Set([key(center)])
    const queue = [center]
    while (queue.length > 0) {
      const current = queue.shift()!
      for (const { id } of previewHexDirections) {
        const next = stepPreviewHex(current, id)
        if (next === null) continue
        expect(known.has(key(next))).toBe(true)
        if (!visited.has(key(next))) {
          visited.add(key(next))
          queue.push(next)
        }
      }
    }
    expect(visited).toEqual(known)
  })
})
