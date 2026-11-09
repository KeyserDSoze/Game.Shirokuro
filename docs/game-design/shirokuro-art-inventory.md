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
 
## Texture terreni nella scena 3D (issue #12)

Atlas leggero `terrain-original-atlas.webp` con disposizione 3×2 da
`tasselli/black.png`, `white.png`, `city.png`, `sand.png`, `water.png`
e `wild.png`, in quest'ordine; celle WebP di riferimento da 64×64 pixel.
Il frontend mostra le texture sulle facce superiori degli esagoni reali
Three.js, mantenendo i lati tridimensionali. L'assegnazione alle celle
è *solo dimostrativa*, modificabile localmente, senza regole di terreno
o visibilità approvate. Il generatore è `scripts/build_terrain_atlas.py`;
l'issue #4 segue l'importazione completa e l'autorizzazione all'uso pubblico.
 
## Schede abilità originali — anteprima 2D (issue #14)

Le 28 immagini `screens2/` sono adesso presenti nel frontend come **un
atlas AVIF di riferimento** in `src/frontend/public/art/hero-ability-cards-original.avif`.
La galleria mostra fronte e retro dell'eroe selezionato, seguendo l'ordine
di `src/shared/heroes.json`: 14 front (indici 0–13) e 14 retro
(indici 14–27). Lo sprite è 4 colonne × 7 righe, celle 320×125 pixel,
dimensione totale 1280×875, circa 18,6KB. Generatore:
`scripts/build_hero_card_atlas.py /percorso/allo/shirokurozip.zip`.

**Attenzione:** la scala ridotta serve a identificare la grafica, **non** a
leggere le istruzioni. I testi nelle immagini non sono ancora digitalizzati
in modo accessibile, verificati con l'autore, né trasformati in regole di
combattimento/movimento. Mancano anche le carte movimento complete
(`carte movimento3/`), i ritratti dettagliati, licenze/permessi di
pubblicazione: continuano a essere tracciati in #3/#4/#5.

## Carte movimento originali (issue #16 e bug #17)

Nell'archivio reale `carte movimento3/` contiene **13 gruppi di dieci
immagini numerate 1..10**; tutti i 14 eroi restano nel roster, ma la
cartella di Gordunhao non contiene carte. Le altre 13 cartelle coincidono
coi nomi canonici, inclusi `Jia Oshi`, `La Morte`, `Magjistar`,
`Sangar Tedra`, `Vlko Dlak`.

L'atlas frontend `movement-cards-original.avif` contiene 130 miniature
di riferimento in ordine row-major: le righe seguono
`src/shared/heroes.json` **escludendo esclusivamente Gordunhao**,
le colonne sono le carte 1..10. Formato AVIF 560×1196 px, celle
56×92 px, ~14,6 KB. È un inventario visivo, **non** un mazzo di gioco
né una descrizione di mosse consentite. Le icone e il significato delle
carte richiedono il regolamento originale.

Nota #17: il ZIP fornito contiene 358 entry (incluse cartelle e dati
di archivio), ~335,7 MB decompressi e un asset accessorio di 23,9MB.
La soglia per singola entry della pipeline è 32 MiB (prima 16); i
controlli del formato e dell'input rimangono attivi. Il ZIP completo
resta fuori dalla repository.

