import { describe, expect, it } from 'vitest'
import { heroRoster } from './catalog'
import {
  movementCardHeroes, movementCardSlot, originalMovementCardSourcePath,
  MOVEMENT_ATLAS_COLUMNS, MOVEMENT_ATLAS_ROWS, MOVEMENT_CARDS_PER_HERO,
  MISSING_MOVEMENT_HERO_ID,
} from './movementAtlas'

describe('movement card references from the original Shirokuro ZIP', () => {
  it('includes all 13 source hero sets, excludes Gordunhao without inventing cards', () => {
    expect(heroRoster).toHaveLength(14)
    expect(movementCardHeroes).toHaveLength(13)
    expect(MOVEMENT_ATLAS_ROWS).toBe(13)
    expect(MOVEMENT_ATLAS_COLUMNS).toBe(10)
    expect(MOVEMENT_CARDS_PER_HERO).toBe(10)
    expect(heroRoster.some(hero => hero.id === MISSING_MOVEMENT_HERO_ID)).toBe(true)
    expect(movementCardHeroes.some(hero => hero.id === MISSING_MOVEMENT_HERO_ID)).toBe(false)
  })

  it('addresses all 130 original cards exactly once in 10-column source order', () => {
    const slots = movementCardHeroes.flatMap(hero =>
      Array.from({ length: MOVEMENT_CARDS_PER_HERO }, (_, index) =>
        movementCardSlot(hero.id, index + 1)),
    )
    expect(slots).toHaveLength(130)
    expect(slots.every(slot => slot !== null)).toBe(true)
    expect(new Set(slots.map(slot => slot?.index)).size).toBe(130)
    expect(slots.map(slot => slot?.index)).toEqual(Array.from({ length: 130 }, (_, i) => i))
  })

  it.each([
    ['adsiz', 1, 0, 0],
    ['adsiz', 10, 9, 0],
    ['howler', 1, 0, 2],
    ['kira', 1, 0, 4],
    ['kira', 10, 9, 4],
    ['magjistar', 4, 3, 8],
    ['vlko-dlak', 10, 9, 12],
  ] as const)('%s movement card %i maps to column %i and row %i',
    (heroId, number, column, row) => {
      expect(movementCardSlot(heroId, number)).toMatchObject({ column, row })
    },
  )

  it.each([
    ['adsiz', 1, 'carte movimento3/Adsiz/1.png'],
    ['jia-oshi', 10, 'carte movimento3/Jia Oshi/10.png'],
    ['magjistar', 7, 'carte movimento3/Magjistar/7.png'],
    ['vlko-dlak', 10, 'carte movimento3/Vlko Dlak/10.png'],
  ] as const)('tracks the original ZIP path for %s card %i', (heroId, number, expected) => {
    expect(originalMovementCardSourcePath(heroId, number)).toBe(expected)
  })

  it.each([0, 11, -1, 1.5, NaN, Infinity])('rejects invalid card number %s', number => {
    expect(movementCardSlot('kira', number)).toBeNull()
    expect(originalMovementCardSourcePath('kira', number)).toBeNull()
  })

  it('does not return cards for unknown IDs or the absent Gordunhao source set', () => {
    expect(movementCardSlot('gordunhao', 1)).toBeNull()
    expect(movementCardSlot('gordunhao', 10)).toBeNull()
    expect(originalMovementCardSourcePath('gordunhao', 1)).toBeNull()
    expect(movementCardSlot('unknown', 1)).toBeNull()
    expect(originalMovementCardSourcePath('../Kira', 1)).toBeNull()
  })
})

