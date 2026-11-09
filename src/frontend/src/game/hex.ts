export type HexCoordinate = Readonly<{ q: number; r: number }>

export function hexDistance(a: HexCoordinate, b: HexCoordinate): number {
  const dq = a.q - b.q
  const dr = a.r - b.r
  return (Math.abs(dq) + Math.abs(dr) + Math.abs(dq + dr)) / 2
}

export function createHexBoard(radius: number): HexCoordinate[] {
  if (!Number.isInteger(radius) || radius < 0 || radius > 6) {
    throw new RangeError('Preview radius must be an integer from 0 to 6')
  }
  const result: HexCoordinate[] = []
  for (let q = -radius; q <= radius; q++) {
    const minR = Math.max(-radius, -q - radius)
    const maxR = Math.min(radius, -q + radius)
    for (let r = minR; r <= maxR; r++) result.push({ q, r })
  }
  return result
}

export function axialToWorld({ q, r }: HexCoordinate) {
  return { x: Math.sqrt(3) * (q + r / 2), z: 1.5 * r }
}

