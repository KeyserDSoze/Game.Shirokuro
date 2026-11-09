import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useTheme } from 'next-themes'
import { Compass, EyeOff, Globe2, Hexagon, Moon, Sun, Swords } from 'lucide-react'
import HexWorld from '@/game/HexWorld'
import HeroSelector from '@/features/heroes/HeroSelector'
import { heroRoster } from '@/features/heroes/catalog'
import type { HexCoordinate } from '@/game/hex'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

function ThemeButton() {
  const { resolvedTheme, setTheme } = useTheme()
  const { t } = useTranslation()
  const [mounted, setMounted] = useState(false)

  useEffect(() => setMounted(true), [])

  return (
    <Button
      type="button"
      variant="outline"
      size="icon"
      data-testid="theme-toggle"
      aria-label={t('themeToggle')}
      onClick={() => setTheme(resolvedTheme === 'dark' ? 'light' : 'dark')}
    >
      {mounted && resolvedTheme === 'dark' ? <Sun /> : <Moon />}
    </Button>
  )
}

export default function App() {
  const { t, i18n } = useTranslation()
  const [selected, setSelected] = useState<HexCoordinate>({ q: 0, r: 0 })
  const [selectedHeroId, setSelectedHeroId] = useState(heroRoster[0].id)
  const selectedHero = heroRoster.find((hero) => hero.id === selectedHeroId) ?? heroRoster[0]

  useEffect(() => { document.documentElement.lang = i18n.language }, [i18n.language])

  return (
    <div className="min-h-screen bg-background text-foreground">
      <header className="border-b border-border bg-card/70">
        <div className="mx-auto flex max-w-[1500px] flex-wrap items-center justify-between gap-4 px-4 py-4 sm:px-8">
          <div className="flex items-center gap-3">
            <div aria-hidden="true" className="relative flex size-11 items-center justify-center overflow-hidden rounded-xl border border-border bg-[#1b1e29] shadow-sm">
              <div className="absolute left-0 h-full w-1/2 bg-[#f5f0e5]" />
              <Hexagon className="relative z-10 size-7 text-[#a99bf0] drop-shadow-md" strokeWidth={2.4} />
            </div>
            <div>
              <h1 className="text-xl font-black tracking-[0.13em]">SHIROKURO <span className="font-normal tracking-normal text-muted-foreground">白黒</span></h1>
              <p className="text-xs text-muted-foreground">{t('tagline')}</p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <span className="mr-2 hidden rounded-full border border-border px-3 py-1 text-xs font-semibold uppercase tracking-[0.15em] text-muted-foreground sm:inline">{t('preview')}</span>
            <Button variant="outline" onClick={() => void i18n.changeLanguage(i18n.language === 'it' ? 'en' : 'it')} aria-label={t('languageToggle')} data-testid="language-toggle">
              <Globe2 />
              <span>{i18n.language.toUpperCase()}</span>
            </Button>
            <ThemeButton />
          </div>
        </div>
      </header>

      <main className="mx-auto grid max-w-[1500px] grid-cols-1 items-start gap-5 px-4 py-6 sm:px-8 lg:grid-cols-[255px_minmax(0,1fr)_255px]">
        <aside className="flex flex-col gap-5">
          <Card>
            <CardHeader>
              <div className="mb-3 flex size-11 items-center justify-center rounded-xl bg-secondary"><Swords className="size-5" /></div>
              <CardTitle>{t('mode')}</CardTitle>
              <CardDescription>{t('modeObjective')}</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="flex items-center justify-between rounded-lg border bg-secondary/60 px-3 py-3 text-sm font-medium">
                <span>{t('pvp')}</span><span className="size-2 rounded-full bg-violet-400" aria-hidden="true" />
              </div>
            </CardContent>
          </Card>
          <HeroSelector selectedId={selectedHeroId} onSelect={setSelectedHeroId} />
        </aside>

        <section className="min-w-0">
          <div className="mb-3 flex items-baseline justify-between gap-3">
            <div>
              <h2 className="text-lg font-bold tracking-tight">{t('board')}</h2>
              <p className="text-sm text-muted-foreground">{t('boardHint')}</p>
            </div>
          </div>
          <div className="relative h-[min(65vh,660px)] min-h-[420px] overflow-hidden rounded-2xl border border-[#414254] bg-[#171b26] shadow-2xl shadow-black/10" data-testid="world-stage">
            <div className="pointer-events-none absolute left-4 top-4 z-10 rounded-lg border border-white/15 bg-[#171b26]/80 px-3 py-2 text-[11px] font-semibold uppercase tracking-[0.2em] text-white/80 backdrop-blur-sm">{t('preview')}</div>
            <HexWorld selected={selected} onSelect={setSelected} />
            <div data-testid="world-hero" aria-live="polite" className="pointer-events-none absolute right-4 top-4 z-10 max-w-[55%] rounded-lg border border-white/15 bg-[#171b26]/80 px-3 py-2 text-right text-xs font-medium text-white/90 backdrop-blur-sm">{t('heroPreview', { name: selectedHero.name })}</div>
            <div className="pointer-events-none absolute bottom-4 left-4 right-4 z-10 rounded-lg border border-white/10 bg-[#171b26]/75 px-3 py-2 text-center text-xs text-white/75 backdrop-blur-sm">{t('controls')}</div>
          </div>
        </section>

        <aside className="flex flex-col gap-5">
          <Card>
            <CardHeader>
              <div className="mb-3 flex size-11 items-center justify-center rounded-xl bg-secondary"><Compass className="size-5" /></div>
              <CardTitle>{t('selection')}</CardTitle>
              <CardDescription>{t('selectionHint')}</CardDescription>
            </CardHeader>
            <CardContent>
              <div data-testid="selected-hex" className="grid grid-cols-2 gap-3">
                <div className="rounded-lg bg-secondary p-3">
                  <span className="block text-xs text-muted-foreground">Q</span>
                  <span className="text-2xl font-bold tabular-nums">{selected.q}</span>
                </div>
                <div className="rounded-lg bg-secondary p-3">
                  <span className="block text-xs text-muted-foreground">R</span>
                  <span className="text-2xl font-bold tabular-nums">{selected.r}</span>
                </div>
              </div>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <div className="mb-3 flex size-11 items-center justify-center rounded-xl bg-secondary"><EyeOff className="size-5" /></div>
              <CardTitle>{t('hidden')}</CardTitle>
              <CardDescription>{t('hiddenHint')}</CardDescription>
            </CardHeader>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>{t('status')}</CardTitle>
              <CardDescription>{t('statusHint')}</CardDescription>
            </CardHeader>
          </Card>
        </aside>
      </main>

      <footer className="mx-auto max-w-[1500px] px-4 pb-7 text-center text-xs text-muted-foreground sm:px-8">{t('footer')}</footer>
    </div>
  )
}


