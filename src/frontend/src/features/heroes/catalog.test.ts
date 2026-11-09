import { describe, expect, it } from 'vitest'
import { heroRoster, getHeroById, availablePortraits } from './catalog'

describe('hero roster from original art', () => {
  it('contains exactly 14 unique canonical IDs and real names', () => {
    expect(heroRoster).toHaveLength(14)
    expect(new Set(heroRoster.map(({ id }) => id)).size).toBe(14)
    expect(heroRoster.every(({ id, name }) => /^[a-z]+(?:-[a-z]+)*$/.test(id) && name.length > 1)).toBe(true)
  })

  it.each(['kira', 'krugg', 'gordunhao', 'magjistar', 'jia-oshi'])('resolves %s exactly', (id) => {
    expect(getHeroById(id)?.id).toBe(id)
  })

  it('rejects unknown IDs rather than inventing an ability or character', () => {
    expect(getHeroById('wanderer')).toBeUndefined()
    expect(getHeroById('../secret')).toBeUndefined()
  })

  it('starts without web portraits until verified assets are imported', () => {
    expect(availablePortraits.size).toBe(0)
  })
})

