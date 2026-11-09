import { describe, expect, it } from 'vitest'
import { heroRoster } from './catalog'
import {
  HERO_CARD_COLUMNS, HERO_CARD_ROWS, heroCardAtlasSlot,
  originalHeroCardSourcePath, type CardFace,
} from './cardAtlas'

describe('original hero ability sheet front/back atlas', () => {
  it('covers all 14 heroes and two faces exactly once in a 4×7 atlas', () => {
    const slots = heroRoster.flatMap(hero =>
      (['front', 'back'] as const).map(face => heroCardAtlasSlot(hero.id, face)))
    expect(slots).toHaveLength(28)
    expect(slots.every(slot => slot !== null)).toBe(true)
    expect(new Set(slots.map(slot => `${slot?.row}:${slot?.column}`)).size)
      .toBe(HERO_CARD_COLUMNS * HERO_CARD_ROWS)
  })

  it.each([
    ['adsiz', 'front', 0, 0],
    ['kira', 'front', 1, 1],
    ['vlko-dlak', 'front', 1, 3],
    ['adsiz', 'back', 2, 3],
    ['kira', 'back', 3, 4],
    ['vlko-dlak', 'back', 3, 6],
  ] as const)('maps %s %s to column %i and row %i', (id, face, column, row) => {
    expect(heroCardAtlasSlot(id, face)).toMatchObject({ column, row })
  })

  it.each([
    ['kira', 'front', 'screens2/kira.png'],
    ['kira', 'back', 'screens2/back kira.png'],
    ['jia-oshi', 'front', 'screens2/jia oshi.png'],
    ['vlko-dlak', 'back', 'screens2/back vlko dlak.png'],
    ['gordunhao', 'front', 'screens2/gordunhao.png'],
    ['magjistar', 'back', 'screens2/back magjistar.png'],
  ] as const)('tracks source filename for %s %s', (id, face, sourcePath) => {
    expect(originalHeroCardSourcePath(id, face)).toBe(sourcePath)
  })

  it('rejects unknown hero or face instead of showing someone else\'s sheet', () => {
    expect(heroCardAtlasSlot('unlisted', 'front')).toBeNull()
    expect(heroCardAtlasSlot('../kira', 'back')).toBeNull()
    expect(originalHeroCardSourcePath('kira', 'invalid' as CardFace)).toBeNull()
  })
})

