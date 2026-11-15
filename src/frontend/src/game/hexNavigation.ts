import { hexDistance, type HexCoordinate } from './hex'
import { PREVIEW_MAP_RADIUS } from './mapDocument'

/**
 * Visual-only pointy-top axial directions, consistent with axialToWorld.
 * Do not interpret these steps as permitted PvP movement or turn actions.
 */
export const previewHexDirections = [
  { id: 'east', dq: 1, dr: 0, labelKey: 'hexEast' },
  { id: 'northEast', dq: 1, dr: -1, labelKey: 'hexNorthEast' },
  { id: 'northWest', dq: 0, dr: -1, labelKey: 'hexNorthWest' },
  { id: 'west', dq: -1, dr: 0, labelKey: 'hexWest' },
  { id: 'southWest', dq: -1, dr: 1, labelKey: 'hexSouthWest' },
  { id: 'southEast', dq: 0, dr: 1, labelKey: 'hexSouthEast' },
] as const

export type PreviewHexDirection = (typeof previewHexDirections)[number]['id']

const center: HexCoordinate = { q: 0, r: 0 }

export function isPreviewHexCoordinate(cell: HexCoordinate): boolean {
  return Number.isInteger(cell.q) && Number.isInteger(cell.r) &&
    hexDistance(center, cell) <= PREVIEW_MAP_RADIUS
}

/**
 * Navigate exactly one adjacent hex without ever leaving the 19-cell local
 * preview. A forbidden step has no selection effect. The function is pure.
 */
export function stepPreviewHex(
  selected: HexCoordinate, direction: PreviewHexDirection,
): HexCoordinate | null {
  if (!isPreviewHexCoordinate(selected)) return null
  const delta = previewHexDirections.find(move => move.id === direction)
  if (!delta) return null
  const next = { q: selected.q + delta.dq, r: selected.r + delta.dr }
  return isPreviewHexCoordinate(next) ? next : null
}
