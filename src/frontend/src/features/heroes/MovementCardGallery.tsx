import { useTranslation } from 'react-i18next'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { getHeroById } from './catalog'
import {
  movementCardSlot, MOVEMENT_ATLAS_PATH, MOVEMENT_ATLAS_COLUMNS,
  MOVEMENT_ATLAS_ROWS, MOVEMENT_CARD_HEIGHT, MOVEMENT_CARD_WIDTH,
  MOVEMENT_CARDS_PER_HERO,
} from './movementAtlas'

type Props = { heroId: string }

const cardNumbers = Array.from({ length: MOVEMENT_CARDS_PER_HERO }, (_, i) => i + 1)

export default function MovementCardGallery({ heroId }: Props) {
  const { t } = useTranslation()
  const hero = getHeroById(heroId)
  if (!hero) return null

  const available = movementCardSlot(hero.id, 1) !== null

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('movementCardsTitle', { name: hero.name })}</CardTitle>
        <CardDescription>{t('movementCardsDescription')}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {available ? (
          <div
            role="group"
            aria-label={t('movementCardsGroup', { name: hero.name })}
            className="grid grid-cols-3 justify-items-center gap-2 sm:grid-cols-5"
            data-testid="movement-card-gallery"
          >
            {cardNumbers.map(number => {
              const slot = movementCardSlot(hero.id, number)
              if (!slot) return null

              return (
                <figure key={number} className="flex min-w-0 flex-col items-center gap-1">
                  <div
                    role="img"
                    aria-label={t('movementCardAlt', { name: hero.name, number })}
                    data-testid={'movement-card-' + number}
                    data-hero-id={hero.id}
                    className="overflow-hidden rounded-md border border-border bg-[#eeece5]"
                    style={{
                      width: MOVEMENT_CARD_WIDTH,
                      height: MOVEMENT_CARD_HEIGHT,
                      backgroundImage: 'url(' + import.meta.env.BASE_URL + MOVEMENT_ATLAS_PATH + ')',
                      backgroundRepeat: 'no-repeat',
                      backgroundSize: (MOVEMENT_ATLAS_COLUMNS * MOVEMENT_CARD_WIDTH) +
                        'px ' + (MOVEMENT_ATLAS_ROWS * MOVEMENT_CARD_HEIGHT) + 'px',
                      backgroundPosition: '-' + (slot.column * MOVEMENT_CARD_WIDTH) +
                        'px -' + (slot.row * MOVEMENT_CARD_HEIGHT) + 'px',
                    }}
                  />
                  <figcaption className="text-xs font-medium text-muted-foreground">
                    {t('movementCardNumber', { number })}
                  </figcaption>
                </figure>
              )
            })}
          </div>
        ) : (
          <p
            data-testid="movement-cards-missing"
            role="status"
            className="rounded-md border border-dashed p-3 text-sm text-muted-foreground"
          >
            {t('movementCardsMissing', { name: hero.name })}
          </p>
        )}
        <p data-testid="movement-cards-warning" className="text-xs text-muted-foreground">
          {t('movementCardsWarning')}
        </p>
      </CardContent>
    </Card>
  )
}

