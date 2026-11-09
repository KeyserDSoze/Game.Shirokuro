import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { getHeroById } from '@/features/heroes/catalog'
import {
  guestLobbyApi, LobbyHttpError, parseInviteCode, parseTeamIds,
  type GuestSession, type LobbyRoster,
} from './api'

type Props = { previewHeroId: string }

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

  useEffect(() => {
    let active = true
    // Read-only: never create a guest identity automatically on page load.
    void guestLobbyApi.session()
      .then(identity => { if (active && identity.kind === 'guest') setSession(identity) })
      .catch(() => { /* API may be offline when exploring the local preview. */ })
    return () => { active = false }
  }, [])

  async function run(action: () => Promise<void>) {
    if (busy) return
    setBusy(true)
    setMessage('')
    try {
      await action()
    } catch (error) {
      if (error instanceof LobbyHttpError) {
        const key = error.status === 409 ? 'lobbyConflict' :
          error.status === 404 ? 'lobbyMissing' :
          error.status === 429 ? 'lobbyLimited' :
          error.status === 401 || error.status === 403 ? 'lobbyDenied' :
          'lobbyInvalid'
        setMessage(t(key))
      } else {
        setMessage(t('lobbyInvalid'))
      }
    } finally {
      setBusy(false)
    }
  }

  function showRoom(next: LobbyRoster) {
    setRoom(next)
    setInvite(next.lobbyId)
    const own = next.participants.find(p => p.playerId === session?.playerId)
    setNewTeam(own?.teamId ?? next.teamIds[0])
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
            onClick={() => void run(async () => {
              const own = await guestLobbyApi.signIn()
              setSession(own)
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
                  onClick={() => void run(async () => {
                    const result = await guestLobbyApi.create(parseTeamIds(teamsText), creatorTeam.trim())
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
                    onClick={() => void run(async () => {
                      const result = await guestLobbyApi.join(parseInviteCode(invite), invitedTeam.trim())
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
                  onClick={() => void run(async () => {
                    showRoom(await guestLobbyApi.read(room.lobbyId))
                    setMessage(t('lobbyRefreshed'))
                  })}
                >
                  {t('lobbyRefresh')}
                </Button>
                <Button
                  type="button"
                  data-testid="lobby-pick-hero"
                  disabled={busy}
                  onClick={() => void run(async () => {
                    showRoom(await guestLobbyApi.chooseHero(room.lobbyId, previewHeroId, room.revision))
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
                  onClick={() => void run(async () => {
                    showRoom(await guestLobbyApi.changeTeam(room.lobbyId, newTeam, room.revision))
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
                  onClick={() => void run(async () => {
                    await guestLobbyApi.leave(room.lobbyId, room.revision)
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
              onClick={() => void run(async () => {
                await guestLobbyApi.signOut()
                setSession(null)
                setRoom(null)
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

