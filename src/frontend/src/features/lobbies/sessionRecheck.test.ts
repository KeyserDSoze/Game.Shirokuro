import { describe, expect, it } from 'vitest'
import {
  SESSION_RECHECK_MIN_INTERVAL_MS, sessionRecheckDelayMs,
} from './sessionRecheck'

describe('minimum interval between authenticated session hints (#122)', () => {
  it('permits the first focus or broadcast check immediately', () => {
    expect(SESSION_RECHECK_MIN_INTERVAL_MS).toBe(2_000)
    expect(sessionRecheckDelayMs(null, 0)).toBe(0)
    expect(sessionRecheckDelayMs(null, 5_000)).toBe(0)
  })

  it.each([
    [1_000, 1_000, 2_000],
    [1_000, 1_500, 1_500],
    [1_000, 2_999, 1],
    [1_000, 2_999.9, 1],
    [1_000, 3_000, 0],
    [1_000, 3_001, 0],
    [1_000, 999, 2_000],
  ])('last started %s, now %s -> wait %s ms', (last, now, expected) => {
    expect(sessionRecheckDelayMs(last, now)).toBe(expected)
  })

  it.each([
    [Number.NaN, 1_000],
    [Number.POSITIVE_INFINITY, 1_000],
    [100, Number.NaN],
    [100, Number.NEGATIVE_INFINITY],
  ])('invalid monotonic timestamp %s/%s never opens an unbounded request burst', (last, now) => {
    expect(sessionRecheckDelayMs(last, now)).toBe(2_000)
  })
})
