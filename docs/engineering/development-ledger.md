# Shirokuro development ledger

GitHub Issues remain the source of detailed truth. This file is a compact index.

| Issue | Version | Status | Scope | Verification / next action |
|---|---|---|---|---|
| [#1](https://github.com/KeyserDSoze/Game.Shirokuro/issues/1) | 0.0.1 | Done | Repository foundation, .NET 10, React/TypeScript, shadcn/ui, R3F hex preview, tests and CI | PR #2 merged; CI #42649038047 green: 11 xUnit, 12 Vitest, 3 Playwright; builds pass. |
| [#3](https://github.com/KeyserDSoze/Game.Shirokuro/issues/3) | 0.0.1+ | In progress | Formalize game rules, modes, heroes and private observations | ZIP and first rules received 09/11/2026; see docs/game-design and docs/gameplay, await detailed design clarification. |
| [#4](https://github.com/KeyserDSoze/Game.Shirokuro/issues/4) | 0.0.1+ | In progress | Optimize and import original hero, hex tile and card artwork | Safe exporter tested against original ZIP (14 portraits + 6 textures, ~0.8 MB) and 7 synthetic Python tests; derived images not yet committed to GitHub. |
| [#5](https://github.com/KeyserDSoze/Game.Shirokuro/issues/5) | 0.0.1+ | In progress | First PvP treasure mode: game design and authoritative rules | Progression level 1→2 is an isolated domain value object with xUnit tests; all match/secret/death/treasure logic still undefined. |
| [#7](https://github.com/KeyserDSo/Game.Shirokuro/issues/7) | 0.0.1 | Done | Accessible 14-hero preview picker | Main CI #42657389085 green: 18 xUnit, 20 Vitest, 5 Playwright, 7 Python tests; original art integration remains #4. |
| [#8](https://github.com/KeyserDSoze/Game.Shirokuro/issues/8) | 0.0.1 | Done | Server-authoritative hidden-state observation projection | CI #42660793662 green: 37 xUnit, 20 Vitest, 5 Playwright, 7 Python; Domain snapshot + Application default-deny projection. No public match endpoint. |
| [#9](https://github.com/KeyserDSoze/Game.Shirokuro/issues/9) | To plan | Planned | Authenticated SignalR / player-scoped observations without private leaks | Blocked on identity and visibility policy from #3/#5; future integration uses #8 projector. |
| [#10](https://github.com/KeyserDSoze/Game.Shirokuro/issues/10) | 0.0.1 | In progress | Immutable .NET lobby, configurable teams & hero picks | Domain/Application in-memory coordinator and xUnit regression tests; awaiting CI, no unauthenticated endpoints. |

