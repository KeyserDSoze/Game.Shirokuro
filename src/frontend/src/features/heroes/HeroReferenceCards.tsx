import { useTranslation } from 'react-i18next'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { getHeroById } from './catalog'
import {
  heroCardAtlasSlot, HERO_CARD_ATLAS_PATH, HERO_CARD_COLUMNS,
  HERO_CARD_HEIGHT, HERO_CARD_ROWS, HERO_CARD_WIDTH, type CardFace,
} from './cardAtlas'

type Props = { heroId: string }

const faces: readonly CardFace[] = ['front', 'back']

export default function HeroReferenceCards({ heroId }: Props) {
  const { t } = useTranslation()
  const hero = getHeroById(heroId)
  if (!hero) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('heroCardsTitle', { name: hero.name })}</CardTitle>
        <CardDescription>{t('heroCardsDescription')}</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="flex flex-wrap justify-center gap-4">
          {faces.map(face => {
            const slot = heroCardAtlasSlot(hero.id, face)
            if (!slot) return null

            return (
              <figure key={face} className="max-w-full space-y-1">
                <figcaption className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                  {t(`heroCardFaces.${face}`)}
                </figcaption>
                <div
                  role="img"
                  aria-label={t('heroCardAlt', {
                    name: hero.name,
                    face: t(`heroCardFaces.${face}`),
                  })}
                  data-testid={`hero-card-${face}`}
                  data-hero-id={hero.id}
                  className="max-w-full overflow-hidden rounded-lg border border-border bg-[#e8e7e4]"
                  style={{
                    width: HERO_CARD_WIDTH,
                    height: HERO_CARD_HEIGHT,
                    backgroundImage: `url(${import.meta.env.BASE_URL}${HERO_CARD_ATLAS_PATH})`,
                    backgroundRepeat: 'no-repeat',
                    backgroundSize: `${HERO_CARD_COLUMNS * HERO_CARD_WIDTH}px ${HERO_CARD_ROWS * HERO_CARD_HEIGHT}px`,
                    backgroundPosition: `-${slot.column * HERO_CARD_WIDTH}px -${slot.row * HERO_CARD_HEIGHT}px`,
                  }}
                />
              </figure>
            )
          })}
        </div>
        <p data-testid="hero-cards-warning" className="mt-3 text-xs text-muted-foreground">
          {t('heroCardsWarning')}
        </p>
      </CardContent>
    </Card>
  )
}

