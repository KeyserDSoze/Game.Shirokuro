import { heroRoster } from './catalog'

/**
 * Original screens2 artwork (14 front, 14 reverse sheets), visual references
 * only. The associated ability text has not been transcribed or approved as
 * executable game rules.
 */
export const HERO_CARD_ATLAS_PATH = 'art/hero-ability-cards-original.avif'
export const HERO_CARD_WIDTH = 320
export const HERO_CARD_HEIGHT = 125
export const HERO_CARD_COLUMNS = 4
export const HERO_CARD_ROWS = 7

export type CardFace = 'front' | 'back'
export type CardAtlasSlot = Readonly<{
  index: number
  column: number
  row: number
}>

/** Row-major order: all 14 fronts first, then all 14 reverse sheets. */
export function heroCardAtlasSlot(id: string, face: CardFace): CardAtlasSlot | null {
  if (face !== 'front' && face !== 'back') return null
  const heroIndex = heroRoster.findIndex(hero => hero.id === id)
  if (heroIndex < 0) return null
  const index = heroIndex + (face === 'back' ? heroRoster.length : 0)
  if (index >= HERO_CARD_COLUMNS * HERO_CARD_ROWS) return null
  return {
    index,
    column: index % HERO_CARD_COLUMNS,
    row: Math.floor(index / HERO_CARD_COLUMNS),
  }
}

export function originalHeroCardSourcePath(id: string, face: CardFace): string | null {
  if (heroCardAtlasSlot(id, face) === null) return null
  return `screens2/${face === 'back' ? 'back ' : ''}${id.replaceAll('-', ' ')}.png`
}

