import source from '../../../../shared/heroes.json'
import art from '../../generated/art-manifest.json'

export type Hero = Readonly<{ id: string; name: string }>

export const heroRoster: readonly Hero[] = source.heroes
export const availablePortraits: ReadonlySet<string> = new Set(art.heroes)

export function getHeroById(id: string): Hero | undefined {
  return heroRoster.find((hero) => hero.id === id)
}

