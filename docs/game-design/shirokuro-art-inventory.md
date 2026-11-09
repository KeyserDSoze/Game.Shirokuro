# Inventario del materiale grafico originale — 09/11/2026

**Fonte:** archivio `shirokurozip.zip` fornito dall'autore della conversazione. L'archivio (~312 MiB nominali, 326.761.573 byte) contiene **317 file**: 303 PNG e 14 JPG. Il file ZIP originale non viene pubblicato nel repository. Questo documento è solo un indice/provenienza; le conversioni web e l'autorizzazione al riutilizzo pubblico sono tracciate in [#4](https://github.com/KeyserDSoze/Game.Shirokuro/issues/4).

| Cartella originale | File | Contenuto osservato |
|---|---:|---|
| `hero base/` | 14 | Illustrazioni singole dei personaggi (2026 × 2334) |
| `back hero card/` | 14 | Carte con nome e illustrazione dell'eroe |
| `screens2/` | 28 | 14 schermate con testo abilità e potenziamento, più 14 retro |
| `carte movimento3/` | 130 | 13 set di 10 carte di movimento (icone e ambienti) |
| `hero2/` | 15 | Schede con ambienti/colori e un retro |
| `tasselli/` | 6 | Black, white, city, sand, water, wild |
| `maps/` | 2 | `mappa.png` (mappa a colori); `dun.png` (vuoto) |
| `base roba/` | 32 | Texture terreno, elementi di base e schede ausiliarie |
| `numbers2/` | 58 | Numeri, lettere, icone e segnalini tematici |
| `tombstone/` | 14 | Illustrazioni JPG per tombe/eroi |
| `dials/` | 2 | Grafiche del countdown |
| `box2/` | 2 | Grafica della confezione fronte/retro |
| **Totale** | **317** | **303 PNG, 14 JPG** |

## I 14 eroi

| ID canonico | Nome | File illustrazione originale | Scheda delle abilità |
|---|---|---|---|
| `adsiz` | Adsiz | `hero base/Adsiz.png` | `screens2/adsiz.png` |
| `bazmut` | Bazmut | `hero base/Bazmut.png` | `screens2/bazmut.png` |
| `gordunhao` | Gordunhao | `hero base/Gorduhano.png` | `screens2/gordunhao.png` |
| `howler` | Howler | `hero base/Howler.png` | `screens2/howler.png` |
| `jia-oshi` | Jia Oshi | `hero base/Jia Oshi.png` | `screens2/jia oshi.png` |
| `kira` | Kira | `hero base/Kira.png` | `screens2/kira.png` |
| `krugg` | Krugg | `hero base/Krugg.png` | `screens2/krugg.png` |
| `kylia` | Kylia | `hero base/Kylia.png` | `screens2/kylia.png` |
| `la-morte` | La Morte | `hero base/La Morte.png` | `screens2/la morte.png` |
| `magjistar` | Magjistar | `hero base/Magjister.png` | `screens2/magjistar.png` |
| `sandgespenst` | Sandgespenst | `hero base/Sandgespenst.png` | `screens2/sandgespenst.png` |
| `sangar-tedra` | Sangar Tedra | `hero base/Sangar Tedra.png` | `screens2/sangar tedra.png` |
| `shilla` | Shilla | `hero base/Shilla.png` | `screens2/shilla.png` |
| `vlko-dlak` | Vlko Dlak | `hero base/Vlko Dlak.png` | `screens2/vlko dlak.png` |

I nomi canonici sono etichette di lavoro: il materiale contiene grafie diverse, tra cui `Gorduhano/Gordunhao`, `Magjister/Magjistar` e `kurgg/Krugg`. Verificare la grafia ufficiale con l'autore prima della pubblicazione.

## Informazione di gameplay presente nella grafica

Le tavole `screens2/` non sono puramente decorative: contengono testi di abilità e potenziamenti. Per esempio, la tavola di Kira cita il colpire/nascondersi su terreni adiacenti e il potenziamento **Baizo Shi**, che introduce uno sdoppiamento con movimenti noti. Trascrizione dettagliata, semantica delle icone, costo/durata delle abilità e compatibilità con movimento nascosto **non sono ancora confermati**; non convertirli direttamente in regole eseguibili.

## Problemi e limiti rilevati

- `maps/dun.png` ha **0 byte** e non è una PNG leggibile.
- Nella cartella `carte movimento3/Gordunhao/` non sono presenti file, a differenza di altri 13 eroi con 10 carte ciascuno.
- `carte movimento/` contiene cartelle vuote: non confondere con `carte movimento3/` popolata.
- Le illustrazioni originali sono 2D; modelli di eroi realmente 3D dovranno essere creati rispettando il reference art, non reinterpretati come regole.
- Non includere nella bundle frontend 312 MB di PNG. Preparare conversioni ottimizzate, separando miniature, texture e originali di archivio.

## Passo successivo

[#4](https://github.com/KeyserDSoze/Game.Shirokuro/issues/4) guida l'importazione web, gli asset derivati e i test del catalogo. Il file di riferimento `src/shared/heroes.json` registra solo gli ID e i nomi, senza statistiche/abilità inventate.
 
## Prima integrazione web (issue #11)

È stato derivato **un singolo atlas WebP** di 14 miniature dagli originali
`hero base/`. Layout deterministico: 7 colonne × 2 righe, cella sorgente
64×74, 448×148 px, ordine canonico di `src/shared/heroes.json`.
L'atlas è una **miniatura di riferimento** del materiale fornito dall'autore,
non un modello 3D e non una sostituzione degli originali. Il generatore
`scripts/build_hero_atlas.py` riproduce il processo e rifiuta PNG mancanti,
non leggibili o ZIP non sicuri; i relativi test CI usano fixture sintetiche.

L'issue #4 resta aperta per le immagini dettagliate, i terreni, le carte,
gli aspetti di attribuzione/licenza dell'arte e la verifica del rilascio.

