import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  exportPreviewMap, importPreviewMap, MAX_PREVIEW_MAP_JSON_LENGTH,
} from '@/game/mapDocument'
import type { TerrainOverrides } from '@/game/terrain'

type Props = {
  overrides: TerrainOverrides
  onImport: (next: TerrainOverrides) => void
}

type Status = 'exported' | 'imported' | 'invalid' | null

export default function MapDocumentEditor({ overrides, onImport }: Props) {
  const { t } = useTranslation()
  const [draft, setDraft] = useState('')
  const [status, setStatus] = useState<Status>(null)

  function handleImport() {
    try {
      const validated = importPreviewMap(draft)
      onImport(validated)
      setStatus('imported')
    } catch {
      // Do not display the untrusted payload or change the current map.
      setStatus('invalid')
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('mapDocumentTitle')}</CardTitle>
        <CardDescription>{t('mapDocumentHint')}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            size="sm"
            variant="outline"
            data-testid="map-export"
            onClick={() => { setDraft(exportPreviewMap(overrides)); setStatus('exported') }}
          >
            {t('mapDocumentExport')}
          </Button>
          <Button type="button" size="sm" data-testid="map-import" onClick={handleImport}>
            {t('mapDocumentImport')}
          </Button>
        </div>
        <label htmlFor="shirokuro-map-document" className="block text-xs font-medium">
          {t('mapDocumentLabel')}
        </label>
        <textarea
          id="shirokuro-map-document"
          data-testid="map-document"
          value={draft}
          onChange={event => { setDraft(event.target.value); setStatus(null) }}
          spellCheck={false}
          rows={6}
          maxLength={MAX_PREVIEW_MAP_JSON_LENGTH}
          placeholder={t('mapDocumentPlaceholder')}
          className="w-full resize-y rounded-md border bg-background p-2 font-mono text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        />
        <p role="status" aria-live="polite" data-testid="map-document-status" className="text-xs text-muted-foreground">
          {status === 'exported' ? t('mapDocumentExported') :
           status === 'imported' ? t('mapDocumentImported') :
           status === 'invalid' ? t('mapDocumentInvalid') :
           t('mapDocumentPrivacy')}
        </p>
      </CardContent>
    </Card>
  )
}

