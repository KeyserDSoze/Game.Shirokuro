import { heroRoster } from './catalog'

/**
 * Low-bandwidth reference atlas derived from the supplied original art ZIP.
 * Hero order matches src/shared/heroes.json exactly (row-major, seven per row).
 * The game world still uses a procedural 3D hero placeholder.
 */
export const HERO_ATLAS_COLUMNS = 7
export const HERO_ATLAS_ROWS = 2
export const HERO_ATLAS_DISPLAY_WIDTH = 56
export const HERO_ATLAS_DISPLAY_HEIGHT = 64
export const HERO_ATLAS_PATH = 'art/heroes-original-atlas.webp'

export type HeroAtlasSlot = Readonly<{ column: number; row: number; index: number }>

export function heroAtlasSlot(id: string): HeroAtlasSlot | null {
  const index = heroRoster.findIndex(hero => hero.id === id)
  if (index < 0 || index >= HERO_ATLAS_COLUMNS * HERO_ATLAS_ROWS) return null
  return { index, column: index % HERO_ATLAS_COLUMNS, row: Math.floor(index / HERO_ATLAS_COLUMNS) }
}

