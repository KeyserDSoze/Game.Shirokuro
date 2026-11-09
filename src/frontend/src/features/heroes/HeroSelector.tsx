import { Shield } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'
import { availablePortraits, getHeroById, heroRoster } from './catalog'
import { HERO_ATLAS_COLUMNS, HERO_ATLAS_DISPLAY_HEIGHT, HERO_ATLAS_DISPLAY_WIDTH, HERO_ATLAS_PATH, HERO_ATLAS_ROWS, heroAtlasSlot } from './atlas'

type Props = {
  selectedId: string
  onSelect: (id: string) => void
}

function HeroPortrait({ id, name }: { id: string; name: string }) {
  const atlas = heroAtlasSlot(id)
  return (
    <div className="relative flex h-16 w-14 shrink-0 items-center justify-center overflow-hidden rounded-md bg-gradient-to-br from-[#e7dece] via-[#b9addb] to-[#42475a] text-xl font-black text-[#242536]">
      {availablePortraits.has(id) ? (
        <img src={`${import.meta.env.BASE_URL}art/heroes/${id}.webp`} alt="" loading="lazy" className="h-full w-full object-contain" />
      ) : atlas !== null ? (
        <span
          data-testid={`hero-art-${id}`}
          data-art="original-atlas"
          aria-hidden="true"
          className="block h-16 w-14 shrink-0"
          style={{
            backgroundImage: `url(${import.meta.env.BASE_URL}${HERO_ATLAS_PATH})`,
            backgroundSize: `${HERO_ATLAS_COLUMNS * HERO_ATLAS_DISPLAY_WIDTH}px ${HERO_ATLAS_ROWS * HERO_ATLAS_DISPLAY_HEIGHT}px`,
            backgroundPosition: `-${atlas.column * HERO_ATLAS_DISPLAY_WIDTH}px -${atlas.row * HERO_ATLAS_DISPLAY_HEIGHT}px`,
            backgroundRepeat: 'no-repeat',
          }}
        />
      ) : (
        <span aria-hidden="true">{name.charAt(0)}</span>
      )}
    </div>
  )
}

export default function HeroSelector({ selectedId, onSelect }: Props) {
  const { t } = useTranslation()
  const selectedHero = getHeroById(selectedId) ?? heroRoster[0]

  return (
    <Card>
      <CardHeader>
        <div className="mb-3 flex size-11 items-center justify-center rounded-xl bg-secondary"><Shield className="size-5" /></div>
        <CardTitle>{t('hero')}</CardTitle>
        <CardDescription>{t('heroHint')}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="flex items-center gap-3 rounded-lg border bg-secondary/50 p-3">
          <HeroPortrait id={selectedHero.id} name={selectedHero.name} />
          <div className="min-w-0">
            <span className="block truncate font-semibold" data-testid="selected-hero" aria-live="polite">{selectedHero.name}</span>
            <span className="block text-xs text-muted-foreground">{t('heroInitialLevel')}</span>
          </div>
        </div>
        <p className="text-xs text-muted-foreground">{t('heroUpgradeLocked')}</p>
        <div role="group" aria-label={t('heroRoster')} className="grid max-h-[360px] grid-cols-2 gap-2 overflow-y-auto pr-1">
          {heroRoster.map((hero) => (
            <button
              key={hero.id}
              type="button"
              data-testid={`hero-${hero.id}`}
              aria-pressed={hero.id === selectedHero.id}
              aria-label={t('chooseHero', { name: hero.name })}
              onClick={() => onSelect(hero.id)}
              className={cn(
                'flex min-w-0 flex-col items-center gap-1 rounded-lg border p-2 text-center text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                hero.id === selectedHero.id ? 'border-ring bg-accent text-accent-foreground' : 'bg-card hover:bg-secondary',
              )}
            >
              <HeroPortrait id={hero.id} name={hero.name} />
              <span className="max-w-full break-words">{hero.name}</span>
            </button>
          ))}
        </div>
        <p className="text-xs text-muted-foreground">{t('heroSelectionLocal')}</p>
      </CardContent>
    </Card>
  )
}


