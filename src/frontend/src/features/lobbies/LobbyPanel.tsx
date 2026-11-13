import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { getHeroById } from '@/features/heroes/catalog'
import {
  guestLobbyApi, LobbyHttpError, LobbyInputError, parseInviteCode, parseTeamIds,
  type GuestSession, type LobbyRoster,
} from './api'
import {
  LOBBY_ROSTER_POLL_INTERVAL_MS, classifyConflictRosterRefresh,
  classifyRosterPollFailure, isVerifiedLobbyRoster, reconcilePolledRoster, rosterPollDelayMs,
} from './rosterSync'

type Props = { previewHeroId: string }
const SESSION_CHANGE_CHANNEL = 'shirokuro:guest-session-changed'

/** The room is pre-match only; the hex map remains an independent local preview. */
export default function LobbyPanel({ previewHeroId }: Props) {
  const { t } = useTranslation()
  const [session, setSession] = useState<GuestSession | null>(null)
  const [room, setRoom] = useState<LobbyRoster | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [invite, setInvite] = useState('')
  const [teamsText, setTeamsText] = useState('white,black')
  const [creatorTeam, setCreatorTeam] = useState('white')
  const [invitedTeam, setInvitedTeam] = useState('black')
  const [newTeam, setNewTeam] = useState('white')
  const sessionChannelRef = useRef<BroadcastChannel | null>(null)
  // Every identity transition invalidates async results started under the
  // previous guest. The operation counter also prevents an old finally from
  // clearing the busy flag of a newer request.
  const sessionGenerationRef = useRef(0)
  const operationRef = useRef(0)
  const busyRef = useRef(false)
  const currentRoomRef = useRef<LobbyRoster | null>(null)
  // Only passive roster reads can be cancelled; explicit commands are never aborted.
  const passivePollAbortRef = useRef<AbortController | null>(null)
  const roomId = room?.lobbyId
  const actorId = session?.playerId

  // A timer callback must inspect the latest roster, not the revision
  // captured when its room/identity effect was installed.
  useEffect(() => { currentRoomRef.current = room }, [room])

  // Broadcast only an opaque "recheck" signal: NEVER a cookie, player ID,
  // invitation code, roster or session state. Other tabs ask the API.
  useEffect(() => {
    if (typeof BroadcastChannel === 'undefined') return
    const channel = new BroadcastChannel(SESSION_CHANGE_CHANNEL)
    sessionChannelRef.current = channel
    return () => {
      sessionChannelRef.current = null
      channel.close()
    }
  }, [])

  const notifySessionChange = () => {
    sessionChannelRef.current?.postMessage('recheck')
  }

  // Invalidate the old room *before* React updates its rendered state.
  // This drops the passive reader and stale bearer invite, not the guest.
  const forgetRoom = useCallback(() => {
    passivePollAbortRef.current?.abort()
    currentRoomRef.current = null
    setRoom(null)
    setInvite('')
  }, [])

  // A revoked guest loses its room and all in-flight operation authority.
  const forgetGuest = useCallback(() => {
    forgetRoom()
    sessionGenerationRef.current++
    operationRef.current++
    busyRef.current = false
    setBusy(false)
    setSession(null)
  }, [forgetRoom])

  useEffect(() => {
    let active = true
    const generation = sessionGenerationRef.current
    // An initial GET may arrive after an explicit login or a revocation:
    // it must never overwrite a newer server-issued guest identity.
    void guestLobbyApi.session()
      .then(identity => {
        if (active && generation === sessionGenerationRef.current &&
            identity.kind === 'guest') {
          sessionGenerationRef.current++
          setSession(identity)
        }
      })
      .catch(() => { /* API may be offline when exploring the local preview. */ })
    return () => { active = false }
  }, [])

  // A tab can be signed out or signed in when another tab changes its
  // cookie. Every signal only asks the server for the *current* identity;
  // no guest, invite, roster or cookie data is accepted over the channel.
  useEffect(() => {
    let active = true
    let inFlight = false
    let pendingRecheck = false

    const recheck = () => {
      if (!active) return
      if (inFlight) {
        // Coalesce bursts of focus/visibility/BC events, but don't lose a
        // logout/login that occurs while the first /me read is outstanding.
        // Exactly one trailing read is sufficient for any pending burst.
        pendingRecheck = true
        return
      }
      inFlight = true
      const generation = sessionGenerationRef.current
      void guestLobbyApi.session()
        .then(identity => {
          if (!active || generation !== sessionGenerationRef.current) return
          if (!session) {
            if (identity.kind !== 'guest') return
            // Adopt only a newly server-verified signed guest. A stale read
            // cannot overwrite a later login or import another tab's room.
            sessionGenerationRef.current++
            setRoom(null)
            setInvite('')
            setSession(identity)
            setMessage(t('lobbySessionChanged'))
          } else if (identity.kind !== 'guest') {
            forgetGuest()
            setMessage(t('lobbySessionExpired'))
          } else if (identity.playerId !== session.playerId) {
            // A different guest can never inherit a previous room or roster.
            forgetGuest()
            setSession(identity)
            setMessage(t('lobbySessionChanged'))
          }
        })
        .catch(error => {
          // Only a confirmed 401 revokes an existing guest. A signed-out tab
          // stays signed out, and 403/503/fetch failures never invent/logout
          // an identity based on an unavailable server.
          if (active && session &&
              generation === sessionGenerationRef.current &&
              error instanceof LobbyHttpError && error.status === 401) {
            forgetGuest()
            setMessage(t('lobbySessionExpired'))
          }
        })
        .finally(() => {
          inFlight = false
          if (active && pendingRecheck &&
              generation === sessionGenerationRef.current) {
            pendingRecheck = false
            recheck()
          }
        })
    }
    const onVisible = () => {
      if (!document.hidden) recheck()
    }
    // Ignore every non-protocol payload. The channel is a hint, never an
    // identity authority or permission to consume unlimited Redis lookups.
    const onMessage = (event: MessageEvent) => {
      if (event.data === 'recheck') recheck()
    }
    const channel = sessionChannelRef.current
    window.addEventListener('focus', recheck)
    document.addEventListener('visibilitychange', onVisible)
    channel?.addEventListener('message', onMessage)
    return () => {
      active = false
      window.removeEventListener('focus', recheck)
      document.removeEventListener('visibilitychange', onVisible)
      channel?.removeEventListener('message', onMessage)
    }
  }, [session, forgetGuest, t])

  // Authenticated, read-only, member-scoped pre-match synchronization.
  // A single self-scheduling timeout avoids overlapping GETs, and exponential
  // backoff reduces request pressure on API/Redis during failover/outage.
  // No client-side timer is allowed to renew the server's 30-minute room TTL.
  useEffect(() => {
    if (!roomId || !actorId) return
    let active = true
    let timer: number | undefined
    let transientFailures = 0
    let inFlightAbort: AbortController | null = null

    const schedule = (delayMs: number) => {
      if (active) timer = window.setTimeout(() => { void poll() }, delayMs)
    }

    const poll = async () => {
      if (!active) return
      if (document.hidden || busyRef.current) {
        // Hidden tabs and active user actions never issue background reads.
        // Do not clear a previously observed outage just because the UI
        // is temporarily busy or not visible.
        schedule(LOBBY_ROSTER_POLL_INTERVAL_MS)
        return
      }

      const generation = sessionGenerationRef.current
      const operation = operationRef.current
      let terminal = false
      const stillCurrent = () =>
        active && generation === sessionGenerationRef.current &&
        operation === operationRef.current && !busyRef.current &&
        currentRoomRef.current?.lobbyId === roomId

      const controller = new AbortController()
      inFlightAbort = controller
      passivePollAbortRef.current = controller
      try {
        const latest = await guestLobbyApi.peek(
          roomId, currentRoomRef.current?.revision, controller.signal)
        if (!stillCurrent()) return
        // A member-authorized 304 is a successful no-change response.
        if (latest === null) {
          transientFailures = 0
          return
        }
        const current = currentRoomRef.current!
        const result = reconcilePolledRoster(current, latest, actorId)
        if (result === 'invalid') {
          // Even HTTP 200 can carry an unusable DTO during API/proxy
          // regressions: fail closed and back off, never refresh at 15s.
          transientFailures = Math.min(3, transientFailures + 1)
          return
        }
        // Valid, including a stale but well-formed revision, means the API
        // is responsive. Do not penalize an authorized no-change snapshot.
        transientFailures = 0
        if (result === 'updated') {
          setRoom(previous =>
            previous?.lobbyId === roomId && previous.revision < latest.revision
              ? latest : previous)
        } else if (result === 'missing') {
          terminal = true
          forgetRoom()
          setMessage(t('lobbyMissing'))
        }
      } catch (error) {
        // An intentional abort is neither a revocation nor a Redis outage.
        if (controller.signal.aborted || !stillCurrent()) return
        const outcome = classifyRosterPollFailure(
          error instanceof LobbyHttpError ? error.status : undefined)
        if (outcome === 'expired') {
          terminal = true
          forgetGuest()
          setMessage(t('lobbySessionExpired'))
        } else if (outcome === 'missing') {
          terminal = true
          forgetRoom()
          setMessage(t('lobbyMissing'))
        } else {
          // 403, 429, 5xx and offline preserve the authenticated roster.
          // Cap the counter to prevent number overflow in long outages.
          transientFailures = Math.min(3, transientFailures + 1)
        }
      } finally {
        if (inFlightAbort === controller) inFlightAbort = null
        if (passivePollAbortRef.current === controller)
          passivePollAbortRef.current = null
        // An old request may have finished after a user mutation started.
        // Never apply its failure penalty to a newer user operation.
        if (active && !terminal) {
          if (!stillCurrent()) transientFailures = 0
          schedule(rosterPollDelayMs(transientFailures, Math.random()))
        }
      }
    }

    schedule(LOBBY_ROSTER_POLL_INTERVAL_MS)
    return () => {
      active = false
      window.clearTimeout(timer)
      inFlightAbort?.abort()
    }
  }, [roomId, actorId, forgetGuest, forgetRoom, t])

  async function run(action: (isCurrent: () => boolean) => Promise<void>) {
    if (busyRef.current) return
    busyRef.current = true
    // User intent supersedes a passive read; never abort the user's mutation.
    passivePollAbortRef.current?.abort()
    const generation = sessionGenerationRef.current
    const operation = ++operationRef.current
    // Capture the room and identity that initiated this request. After a
    // revision conflict only their authorized roster can be re-read.
    const conflictRoomId = room?.lobbyId
    const conflictPlayerId = session?.playerId
    const isCurrent = () =>
      generation === sessionGenerationRef.current &&
      operation === operationRef.current
    setBusy(true)
    setMessage('')
    try {
      await action(isCurrent)
    } catch (error) {
      // An old 401 must not sign out a newer guest. Neither delayed errors
      // nor successful responses may change state after any identity switch.
      if (!isCurrent()) return
      if (error instanceof LobbyHttpError) {
        if (error.status === 401) {
          // The authoritative guest ticket no longer exists; an old room
          // must not stay visible or prevent explicit reauthentication.
          forgetGuest()
          setMessage(t('lobbySessionExpired'))
          return
        }
        if (error.status === 409 && conflictRoomId && conflictPlayerId) {
          // Never replay a rejected mutation: read the new server revision
          // once, then require a separate explicit player confirmation.
          try {
            const latest = await guestLobbyApi.read(conflictRoomId)
            if (!isCurrent()) return
            // The 409 is authoritative for the rejected *mutation*, not
            // proof that this member has left. A malformed 200 must never
            // be promoted into a false membership-loss signal (#115).
            const current = currentRoomRef.current
            const decision = current?.lobbyId === conflictRoomId
              ? classifyConflictRosterRefresh(current, latest, conflictPlayerId)
              : 'invalid'
            if (decision === 'invalid') {
              setMessage(t('lobbyConflict'))
              return
            }
            if (decision === 'missing') {
              // Only a complete, current, same-room roster can revoke the
              // local room view. Keep the signed guest (not a 401).
              forgetRoom()
              setMessage(t('lobbyMissing'))
              return
            }
            showRoom(latest, conflictRoomId)
            setMessage(t('lobbyConflictRefreshed'))
            return
          } catch (refreshError) {
            if (!isCurrent()) return
            if (refreshError instanceof LobbyHttpError) {
              if (refreshError.status === 401) {
                forgetGuest()
                setMessage(t('lobbySessionExpired'))
                return
              }
              if (refreshError.status === 404) {
                // The confirmed 404 belongs to this already verified room.
                if (currentRoomRef.current?.lobbyId === conflictRoomId)
                  forgetRoom()
                setMessage(t('lobbyMissing'))
                return
              }
            }
            // 403, 503 and offline are not evidence of session revocation.
            // Keep the last known view but require a manual refresh.
            setMessage(t('lobbyConflict'))
            return
          }
        }
        // A 404 for a verified room removes its obsolete invite, too.
        // A 404 while joining an unknown code is not room loss; leave the
        // typed invitation available for a deliberate correction (#116).
        if (error.status === 404 && conflictRoomId &&
            currentRoomRef.current?.lobbyId === conflictRoomId) forgetRoom()
        const key = error.status === 409 ? 'lobbyConflict' :
          error.status === 404 ? 'lobbyMissing' :
          error.status === 429 ? 'lobbyLimited' :
          error.status === 403 ? 'lobbyDenied' :
          [502, 503, 504].includes(error.status) ? 'lobbyUnavailable' :
          'lobbyInvalid'
        setMessage(t(key))
      } else {
        // Local validation runs before fetch: an invalid invite/teams input
        // must not be reported as a connectivity outage. Only unexpected
        // network/fetch failures retain the valid guest and roster and invite
        // a deliberate retry; no mutation is replayed automatically.
        setMessage(t(error instanceof LobbyInputError
          ? 'lobbyInvalid' : 'lobbyUnavailable'))
      }
    } finally {
      if (operation === operationRef.current) {
        busyRef.current = false
        setBusy(false)
      }
    }
  }

  function showRoom(next: LobbyRoster, expectedRoomId?: string) {
    // Explicit 200 responses must not adopt foreign or malformed rosters (#110).
    const previous = currentRoomRef.current
    const minRevision = previous?.lobbyId === next?.lobbyId
      ? previous.revision : undefined
    // The .NET LobbyDefinition TeamIds are immutable for this room. A
    // malformed 200 must not change their IDs or order (#119).
    const expectedTeams = previous?.lobbyId === next?.lobbyId
      ? previous.teamIds : undefined
    if (!session || !isVerifiedLobbyRoster(
      next, session.playerId, expectedRoomId, minRevision, expectedTeams,
    )) throw new Error('Invalid authenticated lobby roster response')
    // A stale passive response must never see a previous room as current
    // between a confirmed user action and the next React effect (#109).
    currentRoomRef.current = next
    setRoom(next)
    setInvite(next.lobbyId)
    const own = next.participants.find(p => p.playerId === session?.playerId)
    setNewTeam(own?.teamId ?? next.teamIds[0])
  }

  // A copy is explicitly user-initiated; never store or publish a private
  // lobby invitation. Ignore late permission results after identity changes.
  async function copyInvitation() {
    const code = room?.lobbyId
    if (!code) return
    const generation = sessionGenerationRef.current
    const operation = operationRef.current
    // The room can be lost while browser clipboard permission is pending.
    // Guest identity and operation may remain unchanged after forgetRoom().
    // Never overwrite authoritative leave/missing feedback for an old invite.
    const isCurrent = () => generation === sessionGenerationRef.current &&
      operation === operationRef.current &&
      currentRoomRef.current?.lobbyId === code
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable')
      await navigator.clipboard.writeText(code)
      if (isCurrent()) setMessage(t('lobbyInviteCopied'))
    } catch {
      if (isCurrent()) setMessage(t('lobbyInviteCopyUnavailable'))
    }
  }

  const hero = getHeroById(previewHeroId)

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('lobbyTitle')}</CardTitle>
        <CardDescription>{t('lobbyDescription')}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {!session ? (
          <Button
            type="button"
            data-testid="lobby-sign-in"
            disabled={busy}
            onClick={() => void run(async isCurrent => {
              const own = await guestLobbyApi.signIn()
              if (!isCurrent()) return
              sessionGenerationRef.current++
              setSession(own)
              notifySessionChange()
              setMessage(t('lobbyWelcome'))
            })}
          >
            {t('lobbySignIn')}
          </Button>
        ) : (
          <>
            <p className="break-all text-xs text-muted-foreground" data-testid="lobby-player">
              {t('lobbyYou', { id: session.playerId })}
            </p>
            {!room ? (
              <div className="space-y-3">
                <label htmlFor="lobby-teams" className="block text-xs font-medium">
                  {t('lobbyTeamIds')}
                </label>
                <input
                  id="lobby-teams"
                  data-testid="lobby-teams"
                  value={teamsText}
                  maxLength={100}
                  onChange={event => setTeamsText(event.target.value)}
                  className="w-full rounded-md border bg-background p-2 text-sm"
                />
                <label htmlFor="lobby-creator-team" className="block text-xs font-medium">
                  {t('lobbyYourTeam')}
                </label>
                <input
                  id="lobby-creator-team"
                  data-testid="lobby-creator-team"
                  value={creatorTeam}
                  maxLength={24}
                  onChange={event => setCreatorTeam(event.target.value)}
                  className="w-full rounded-md border bg-background p-2 text-sm"
                />
                <Button
                  type="button"
                  data-testid="lobby-create"
                  disabled={busy}
                  onClick={() => void run(async isCurrent => {
                    const result = await guestLobbyApi.create(parseTeamIds(teamsText), creatorTeam.trim())
                    if (!isCurrent()) return
                    showRoom(result)
                    setMessage(t('lobbyCreated'))
                  })}
                >
                  {t('lobbyCreate')}
                </Button>
                <div className="border-t pt-3">
                  <label htmlFor="lobby-invite" className="block text-xs font-medium">
                    {t('lobbyInvite')}
                  </label>
                  <input
                    id="lobby-invite"
                    data-testid="lobby-invite-input"
                    autoComplete="off"
                    value={invite}
                    onChange={event => setInvite(event.target.value)}
                    className="mt-1 w-full rounded-md border bg-background p-2 text-xs"
                  />
                  <label htmlFor="lobby-join-team" className="mt-2 block text-xs font-medium">
                    {t('lobbyJoinTeam')}
                  </label>
                  <input
                    id="lobby-join-team"
                    data-testid="lobby-join-team"
                    maxLength={24}
                    value={invitedTeam}
                    onChange={event => setInvitedTeam(event.target.value)}
                    className="mt-1 w-full rounded-md border bg-background p-2 text-sm"
                  />
                  <Button
                    type="button"
                    variant="outline"
                    className="mt-2"
                    data-testid="lobby-join"
                    disabled={busy}
                    onClick={() => void run(async isCurrent => {
                      const code = parseInviteCode(invite)
                      const result = await guestLobbyApi.join(code, invitedTeam.trim())
                      if (!isCurrent()) return
                      showRoom(result, code)
                      setMessage(t('lobbyJoined'))
                    })}
                  >
                    {t('lobbyJoin')}
                  </Button>
                </div>
              </div>
            ) : (
              <div className="space-y-3">
                <p className="text-xs font-semibold">{t('lobbyInvite')}</p>
                <code data-testid="lobby-room-code" className="block break-all rounded-md bg-secondary p-2 text-xs">
                  {room.lobbyId}
                </code>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  data-testid="lobby-copy-invite"
                  disabled={busy}
                  onClick={() => void copyInvitation()}
                >
                  {t('lobbyCopyInvite')}
                </Button>
                <p className="text-xs text-muted-foreground">{t('lobbyShare')}</p>
                <p data-testid="lobby-revision" className="text-xs text-muted-foreground">
                  {t('lobbyRevision', { count: room.revision })}
                </p>
                <div data-testid="lobby-roster" aria-live="polite" className="space-y-1 text-xs">
                  {room.participants.map(player => (
                    <p key={player.playerId} className="break-words rounded-md bg-secondary p-2">
                      {player.playerId === session.playerId ? t('lobbySelf') : t('lobbyGuest')}
                      {' · '}{player.teamId}{' · '}
                      {getHeroById(player.heroId ?? '')?.name ?? t('lobbyNoHero')}
                    </p>
                  ))}
                </div>
                <p className="text-xs text-muted-foreground" data-testid="lobby-auto-refresh">
                  {t('lobbyAutoRefresh')}
                </p>
                <Button
                  type="button"
                  variant="outline"
                  data-testid="lobby-refresh"
                  disabled={busy}
                  onClick={() => void run(async isCurrent => {
                    const result = await guestLobbyApi.read(room.lobbyId)
                    if (!isCurrent()) return
                    showRoom(result, room.lobbyId)
                    setMessage(t('lobbyRefreshed'))
                  })}
                >
                  {t('lobbyRefresh')}
                </Button>
                <Button
                  type="button"
                  data-testid="lobby-pick-hero"
                  disabled={busy}
                  onClick={() => void run(async isCurrent => {
                    const result = await guestLobbyApi.chooseHero(
                      room.lobbyId, previewHeroId, room.revision)
                    if (!isCurrent()) return
                    showRoom(result, room.lobbyId)
                    setMessage(t('lobbyHeroSaved', { name: hero?.name ?? previewHeroId }))
                  })}
                >
                  {t('lobbyConfirmHero', { name: hero?.name ?? previewHeroId })}
                </Button>
                <label htmlFor="lobby-new-team" className="block text-xs font-medium">
                  {t('lobbyYourTeam')}
                </label>
                <select
                  id="lobby-new-team"
                  data-testid="lobby-new-team"
                  value={newTeam}
                  onChange={event => setNewTeam(event.target.value)}
                  className="w-full rounded-md border bg-background p-2 text-sm"
                >
                  {room.teamIds.map(team => <option key={team} value={team}>{team}</option>)}
                </select>
                <Button
                  type="button"
                  variant="outline"
                  data-testid="lobby-change-team"
                  disabled={busy}
                  onClick={() => void run(async isCurrent => {
                    const result = await guestLobbyApi.changeTeam(
                      room.lobbyId, newTeam, room.revision)
                    if (!isCurrent()) return
                    showRoom(result, room.lobbyId)
                    setMessage(t('lobbyTeamSaved'))
                  })}
                >
                  {t('lobbyChangeTeam')}
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  data-testid="lobby-leave"
                  disabled={busy}
                  onClick={() => void run(async isCurrent => {
                    await guestLobbyApi.leave(room.lobbyId, room.revision)
                    if (!isCurrent()) return
                    // Clear the passive poll's current-room identity before
                    // committing the React state change: a late GET/404 after
                    // successful DELETE must not overwrite "lobby left".
                    forgetRoom()
                    setMessage(t('lobbyLeft'))
                  })}
                >
                  {t('lobbyLeave')}
                </Button>
              </div>
            )}
            <Button
              type="button"
              size="sm"
              variant="outline"
              data-testid="lobby-sign-out"
              disabled={busy}
              onClick={() => void run(async isCurrent => {
                await guestLobbyApi.signOut()
                if (!isCurrent()) return
                forgetGuest()
                notifySessionChange()
                setMessage(t('lobbySignedOut'))
              })}
            >
              {t('lobbySignOut')}
            </Button>
          </>
        )}
        <p role="status" aria-live="polite" data-testid="lobby-message"
          className="text-xs text-muted-foreground">
          {message || t('lobbyPrivacy')}
        </p>
      </CardContent>
    </Card>
  )
}

