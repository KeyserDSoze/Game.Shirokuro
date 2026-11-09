# Shirokuro development ledger

GitHub Issues remain the source of detailed truth. This file is a compact index.

| Issue | Version | Status | Scope | Verification / next action |
|---|---|---|---|---|
| [#1](https://github.com/KeyserDSoze/Game.Shirokuro/issues/1) | 0.0.1 | Done | Repository foundation, .NET 10, React/TypeScript, shadcn/ui, R3F hex preview, tests and CI | PR #2 merged; CI #42649038047 green: 11 xUnit, 12 Vitest, 3 Playwright; builds pass. |
| [#3](https://github.com/KeyserDSoze/Game.Shirokuro/issues/3) | 0.0.1+ | In progress | Formalize game rules, modes, heroes and private observations | ZIP and first rules received 09/11/2026; see docs/game-design and docs/gameplay, await detailed design clarification. |
| [#4](https://github.com/KeyserDSoze/Game.Shirokuro/issues/4) | 0.0.1+ | In progress | Optimize and import original hero, hex tile and card artwork | Safe ZIP-to-WebP importer, source mapping and CI tests in progress; source ZIP not committed. |
| [#5](https://github.com/KeyserDSoze/Game.Shirokuro/issues/5) | 0.0.1+ | In progress | First PvP treasure mode: game design and authoritative rules | Progression level 1→2 is an isolated domain value object with xUnit tests; all match/secret/death/treasure logic still undefined. |

| [#7](https://github.com/KeyserDSoze/Game.Shirokuro/issues/7) | 0.0.1 | In progress | Accessible 14-hero preview picker | On main after verification: Vitest roster tests and Playwright pointer/keyboard/mobile; close only after CI. |

