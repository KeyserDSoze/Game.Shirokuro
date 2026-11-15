/**
 * Focus/visibility/cross-tab hints are not session authority. Limit their
 * Redis-backed /sessions/me checks, while retaining one trailing observation
 * so a tab still discovers a logout which happened during an earlier read.
 * This is tab-local load shedding, NOT server-side anti-bot protection.
 */
export const SESSION_RECHECK_MIN_INTERVAL_MS = 2_000

/**
 * Remaining interval since the *start* of the previous check, using the
 * browser's monotonic performance.now clock. Round up so timers never
 * fire early and reschedule in a tight loop at the window boundary.
 */
export function sessionRecheckDelayMs(
  lastStartedAt: number | null, now: number,
): number {
  if (lastStartedAt === null) return 0
  if (!Number.isFinite(lastStartedAt) || !Number.isFinite(now))
    return SESSION_RECHECK_MIN_INTERVAL_MS
  return Math.max(0, Math.min(SESSION_RECHECK_MIN_INTERVAL_MS,
    Math.ceil(SESSION_RECHECK_MIN_INTERVAL_MS - (now - lastStartedAt))))
}
