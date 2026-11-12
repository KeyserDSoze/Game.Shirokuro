import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'
import {
  terrainIds, terrainAtlasSlot,
  TERRAIN_ATLAS_COLUMNS, TERRAIN_ATLAS_ROWS, TERRAIN_ATLAS_PATH,
  type TerrainId,
} from '@/game/terrain'

type Props = {
  selectedTerrain: TerrainId
  brush: TerrainId
  onBrushChange: (brush: TerrainId) => void
  onPaint: () => void
  onReset: () => void
  canUndo: boolean
  canRedo: boolean
  onUndo: () => void
  onRedo: () => void
}

const SWATCH_SIZE = 48

export default function TerrainEditor({
  selectedTerrain, brush, onBrushChange, onPaint, onReset,
  canUndo, canRedo, onUndo, onRedo,
}: Props) {
  const { t } = useTranslation()
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('terrainTitle')}</CardTitle>
        <CardDescription>{t('terrainDescription')}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <p data-testid="terrain-current" aria-live="polite" className="text-sm font-medium">
          {t('terrainCurrent', { name: t(`terrainNames.${selectedTerrain}`) })}
        </p>
        <div role="group" aria-label={t('terrainChoices')} className="grid grid-cols-3 gap-2">
          {terrainIds.map(id => {
            const slot = terrainAtlasSlot(id)
            return (
              <button
                key={id}
                type="button"
                aria-label={t('terrainChoose', { name: t(`terrainNames.${id}`) })}
                aria-pressed={brush === id}
                data-testid={`terrain-brush-${id}`}
                onClick={() => onBrushChange(id)}
                className={cn(
                  'flex min-w-0 flex-col items-center gap-1 rounded-lg border p-1.5 text-center text-xs font-medium transition-colors hover:bg-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                  brush === id ? 'border-ring bg-accent text-accent-foreground' : 'bg-card',
                )}
              >
                <span
                  aria-hidden="true"
                  data-testid={`terrain-art-${id}`}
                  className="block shrink-0 overflow-hidden rounded-md"
                  style={{
                    width: SWATCH_SIZE,
                    height: SWATCH_SIZE,
                    backgroundImage: `url(${import.meta.env.BASE_URL}${TERRAIN_ATLAS_PATH})`,
                    backgroundSize: `${TERRAIN_ATLAS_COLUMNS * SWATCH_SIZE}px ${TERRAIN_ATLAS_ROWS * SWATCH_SIZE}px`,
                    backgroundPosition: `-${slot.column * SWATCH_SIZE}px -${slot.row * SWATCH_SIZE}px`,
                    backgroundRepeat: 'no-repeat',
                  }}
                />
                <span>{t(`terrainNames.${id}`)}</span>
              </button>
            )
          })}
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="button" size="sm" onClick={onPaint} data-testid="terrain-paint">{t('terrainApply')}</Button>
          <Button type="button" size="sm" variant="outline" onClick={onReset} data-testid="terrain-reset">{t('terrainReset')}</Button>
          <Button type="button" size="sm" variant="outline" onClick={onUndo} disabled={!canUndo} data-testid="terrain-undo">{t('terrainUndo')}</Button>
          <Button type="button" size="sm" variant="outline" onClick={onRedo} disabled={!canRedo} data-testid="terrain-redo">{t('terrainRedo')}</Button>
        </div>
        <p className="text-xs text-muted-foreground">{t('terrainLocalNotice')}</p>
      </CardContent>
    </Card>
  )
}

