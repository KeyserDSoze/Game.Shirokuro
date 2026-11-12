import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { getHeroById } from '@/features/heroes/catalog'
import {
  guestLobbyApi, LobbyHttpError, LobbyInputError, parseInviteCode, parseTeamIds,
  type GuestSession, type LobbyRoster,
} from './api'

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

  // Drop every locally cached private room reference if the cookie becomes
  // invalid. Only the server may issue a replacement guest identity.
  const forgetGuest = useCallback(() => {
    sessionGenerationRef.current++
    operationRef.current++
    busyRef.current = false
    setBusy(false)
    setSession(null)
    setRoom(null)
    setInvite('')
  }, [])

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

  // A tab opened before another tab's guest sign-in has no session yet.
  // It still needs to hear the opaque cross-tab signal (or regain focus)
  // and ask the server for the browser's current cookie identity. Never
  // auto-issue a guest or accept identity/room data from BroadcastChannel.
  useEffect(() => {
    if (session) return

    let active = true
    const recheckWithoutSession = () => {
      const generation = sessionGenerationRef.current
      void guestLobbyApi.session()
        .then(identity => {
          if (!active || generation !== sessionGenerationRef.current ||
              identity.kind !== 'guest') return
          // Invalidate old /me or sign-in completions before adopting the
          // newly verified guest; no private room state crosses this boundary.
          sessionGenerationRef.current++
          setRoom(null)
          setInvite('')
          setSession(identity)
          setMessage(t('lobbySessionChanged'))
        })
        // 401: still signed out; 503/offline: no proof of any identity.
        // Keep the explicit guest sign-in button without creating a cookie.
        .catch(() => {})
    }
    const onVisible = () => {
      if (!document.hidden) recheckWithoutSession()
    }
    const channel = sessionChannelRef.current
    window.addEventListener('focus', recheckWithoutSession)
    document.addEventListener('visibilitychange', onVisible)
    channel?.addEventListener('message', recheckWithoutSession)
    return () => {
      active = false
      window.removeEventListener('focus', recheckWithoutSession)
      document.removeEventListener('visibilitychange', onVisible)
      channel?.removeEventListener('message', recheckWithoutSession)
    }
  }, [session, t])

  useEffect(() => {
    if (!session) return

    let active = true
    const recheckOnFocus = () => {
      // A late /me response for an earlier identity must not invalidate or
      // restore a guest who authenticated while the read was pending.
      const generation = sessionGenerationRef.current
      // Another tab can sign out or replace the cookie while this tab keeps
      // a room visible. Query the server on focus; never auto-issue a guest.
      void guestLobbyApi.session()
        .then(identity => {
          if (!active || generation !== sessionGenerationRef.current) return
          if (identity.kind !== 'guest') {
            forgetGuest()
            setMessage(t('lobbySessionExpired'))
          } else if (identity.playerId !== session.playerId) {
            // Different server-issued identity must not inherit the previous
            // guest's invite, room, roster or revision.
            forgetGuest()
            setSession(identity)
            setMessage(t('lobbySessionChanged'))
          }
        })
        .catch(error => {
          // A transient offline/503/403 response is not proof of revocation.
          if (active && generation === sessionGenerationRef.current &&
              error instanceof LobbyHttpError && error.status === 401) {
            forgetGuest()
            setMessage(t('lobbySessionExpired'))
          }
        })
    }
    // Headless/embedded browsers may not reliably fire window.focus when
    // tabs switch; visibilitychange and same-origin credential-free notices
    // cover those cases without polling or persisting identity in storage.
    const onVisible = () => {
      if (!document.hidden) recheckOnFocus()
    }
    const channel = sessionChannelRef.current
    window.addEventListener('focus', recheckOnFocus)
    document.addEventListener('visibilitychange', onVisible)
    channel?.addEventListener('message', recheckOnFocus)
    return () => {
      active = false
      window.removeEventListener('focus', recheckOnFocus)
      document.removeEventListener('visibilitychange', onVisible)
      channel?.removeEventListener('message', recheckOnFocus)
    }
  }, [session, forgetGuest, t])

  async function run(action: (isCurrent: () => boolean) => Promise<void>) {
    if (busyRef.current) return
    busyRef.current = true
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
            if (latest.lobbyId !== conflictRoomId ||
                !latest.participants.some(p => p.playerId === conflictPlayerId)) {
              setRoom(null)
              setMessage(t('lobbyMissing'))
              return
            }
            showRoom(latest)
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
                setRoom(null)
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
        // A missing room is stale, but 403/429/503 do NOT prove logout.
        if (error.status === 404) setRoom(null)
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

  function showRoom(next: LobbyRoster) {
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
    const isCurrent = () => generation === sessionGenerationRef.current &&
      operation === operationRef.current
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
                      const result = await guestLobbyApi.join(parseInviteCode(invite), invitedTeam.trim())
                      if (!isCurrent()) return
                      showRoom(result)
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
                <Button
                  type="button"
                  variant="outline"
                  data-testid="lobby-refresh"
                  disabled={busy}
                  onClick={() => void run(async isCurrent => {
                    const result = await guestLobbyApi.read(room.lobbyId)
                    if (!isCurrent()) return
                    showRoom(result)
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
                    showRoom(result)
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
                    showRoom(result)
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
                    setRoom(null)
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

