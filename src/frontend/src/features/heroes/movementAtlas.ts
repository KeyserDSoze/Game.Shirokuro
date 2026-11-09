import { getHeroById, heroRoster } from './catalog'

/**
 * Preview-only art catalogue for carte movimento3 from the original ZIP.
 * The absence of a Gordunhao set is source data, NOT a movement/game rule.
 */
export const MOVEMENT_ATLAS_PATH = 'art/movement-cards-original.avif'
export const MOVEMENT_CARD_WIDTH = 56
export const MOVEMENT_CARD_HEIGHT = 92
export const MOVEMENT_ATLAS_COLUMNS = 10
export const MOVEMENT_CARDS_PER_HERO = 10
export const MISSING_MOVEMENT_HERO_ID = 'gordunhao'

export const movementCardHeroes = heroRoster.filter(
  hero => hero.id !== MISSING_MOVEMENT_HERO_ID,
)

export const MOVEMENT_ATLAS_ROWS = movementCardHeroes.length

export type MovementCardSlot = Readonly<{
  index: number
  column: number
  row: number
}>

export function movementCardSlot(heroId: string, number: number): MovementCardSlot | null {
  if (!Number.isInteger(number) || number < 1 || number > MOVEMENT_CARDS_PER_HERO) {
    return null
  }

  const row = movementCardHeroes.findIndex(hero => hero.id === heroId)
  if (row === -1) return null

  const column = number - 1
  return { index: row * MOVEMENT_CARDS_PER_HERO + column, row, column }
}

/** File names are strictly derived from the allow-listed shared roster. */
export function originalMovementCardSourcePath(heroId: string, number: number): string | null {
  if (!movementCardSlot(heroId, number)) return null
  const hero = getHeroById(heroId)
  if (!hero) return null
  return 'carte movimento3/' + hero.name + '/' + number + '.png'
}

