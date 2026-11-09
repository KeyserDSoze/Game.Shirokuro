import { describe, expect, it } from 'vitest'
import { heroRoster } from './catalog'
import { HERO_ATLAS_COLUMNS, HERO_ATLAS_ROWS, heroAtlasSlot } from './atlas'

describe('original Shirokuro hero reference atlas mapping', () => {
  it('covers the entire hero roster in unique, row-major slots', () => {
    expect(heroRoster).toHaveLength(HERO_ATLAS_COLUMNS * HERO_ATLAS_ROWS)
    const slots = heroRoster.map(hero => heroAtlasSlot(hero.id))
    expect(slots.every(slot => slot !== null)).toBe(true)
    expect(new Set(slots.map(slot => `${slot?.row}:${slot?.column}`)).size).toBe(14)
  })

  it.each([
    ['adsiz', 0, 0],
    ['kira', 5, 0],
    ['krugg', 6, 0],
    ['kylia', 0, 1],
    ['magjistar', 2, 1],
    ['vlko-dlak', 6, 1],
  ])('%s is mapped to column %i, row %i', (id, column, row) => {
    const slot = heroAtlasSlot(id as string)
    expect(slot).toMatchObject({ column, row })
  })

  it('refuses to map unknown IDs to another character art', () => {
    expect(heroAtlasSlot('unknown')).toBeNull()
    expect(heroAtlasSlot('../secret')).toBeNull()
  })
})

