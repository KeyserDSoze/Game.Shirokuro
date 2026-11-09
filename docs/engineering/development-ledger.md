# Shirokuro development ledger

GitHub Issues remain the source of detailed truth. This file is a compact index.

| Issue | Version | Status | Scope | Verification / next action |
|---|---|---|---|---|
| [#1](https://github.com/KeyserDSoze/Game.Shirokuro/issues/1) | 0.0.1 | Done | Repository foundation, .NET 10, React/TypeScript, shadcn/ui, R3F hex preview, tests and CI | PR #2 merged; CI #42649038047 green: 11 xUnit, 12 Vitest, 3 Playwright; builds pass. |
| [#3](https://github.com/KeyserDSoze/Game.Shirokuro/issues/3) | 0.0.1+ | In progress | Formalize game rules, modes, heroes and private observations | ZIP and first rules received 09/11/2026; see docs/game-design and docs/gameplay, await detailed design clarification. |
| [#4](https://github.com/KeyserDSoze/Game.Shirokuro/issues/4) | 0.0.1+ | In progress | Optimize and import original hero, hex tile and card artwork | 14 original hero thumbnails now shipped in 4.7KB atlas (#11); 20 detailed WebP assets, card artwork and license verification still pending; six terrain textures reach 3D board in #12. |
| [#5](https://github.com/KeyserDSoze/Game.Shirokuro/issues/5) | 0.0.1+ | In progress | First PvP treasure mode: game design and authoritative rules | Progression level 1→2 is an isolated domain value object with xUnit tests; all match/secret/death/treasure logic still undefined. |
| [#7](https://github.com/KeyserDSo/Game.Shirokuro/issues/7) | 0.0.1 | Done | Accessible 14-hero preview picker | Main CI #42657389085 green: 18 xUnit, 20 Vitest, 5 Playwright, 7 Python tests; original art integration remains #4. |
| [#8](https://github.com/KeyserDSoze/Game.Shirokuro/issues/8) | 0.0.1 | Done | Server-authoritative hidden-state observation projection | CI #42660793662 green: 37 xUnit, 20 Vitest, 5 Playwright, 7 Python; Domain snapshot + Application default-deny projection. No public match endpoint. |
| [#9](https://github.com/KeyserDSoze/Game.Shirokuro/issues/9) | To plan | Planned | Authenticated SignalR / player-scoped observations without private leaks | Blocked on identity and visibility policy from #3/#5; future integration uses #8 projector. |
| [#10](https://github.com/KeyserDSoze/Game.Shirokuro/issues/10) | 0.0.1 | Done | Immutable .NET lobby, configurable teams & hero picks | CI #42663330140 green: 66 xUnit, 20 Vitest, 5 Playwright, 7 Python. No unauthenticated endpoints. |
| [#11](https://github.com/KeyserDSoze/Game.Shirokuro/issues/11) | 0.0.1 | Done | Original 14 hero images as low-bandwidth atlas in hero selector | CI #42664975811 green: 66 xUnit, 28 Vitest, 7 Playwright, 10 Python; 4.7KB atlas tracked on main. |
| [#12](https://github.com/KeyserDSoze/Game.Shirokuro/issues/12) | 0.0.1 | Done | Original terrain atlas textured 3D hexes + accessible local painter | CI #42673119924 green: 66 xUnit, 42 Vitest, 10 Playwright, 14 Python; textured 3D hexes, IT/EN and local paint/reset. |
