# Visual map JSON exchange — issue #13

## Purpose and boundaries

This feature supports exchanging and restoring the **non-authoritative**
terrain layout on the 19-tile local preview. It is not a match save,
a movement request, a level-design authority or a server-side map database.

## Format

```json
{
  "schemaVersion": 1,
  "radius": 2,
  "cells": [
    {"q": -2, "r": 0, "terrain": "black"}
  ]
}
```

The example above is intentionally incomplete and is **not** importable.
A valid document must contain exactly **19 unique cells** for
`createHexBoard(2)`, in any order. Export always produces canonical order
and includes all 19 hexes, even those using the default illustrative
terrain. Accepted terrain IDs: black, white, city, sand, water, wild.

## Validation

`importPreviewMap(json)` strictly checks a bounded 8192-character payload,
exact root and cell property sets, a recognized schema/radius, integer
axial coordinates, board membership, terrain allow-list, all 19 cells
and no duplicates. Only validated coordinates (format `q:r`) are used
as keys in a fresh null-prototype map; unchanged default cells are omitted.
Import does not modify live state until the entire document is valid.
Unknown properties such as planned destinations, heroes, secrets, spawn,
base and treasure data are rejected rather than ignored.

The React UI populates a textarea using `exportPreviewMap`; user can
copy/paste the JSON. On invalid import it leaves the existing map unchanged.
No automatic storage, network requests or persistence are performed.

## Future authoritative maps

Gameplay maps must use separately versioned, server-validated schemas after
design approval in #3/#5, never accept this client-side validation as
authorization. Importing terrain does not reveal hidden state.


## Local undo/redo history (#99)

The terrain palette now provides **Undo / Redo** controls (Italian and
English, keyboard and mobile accessible). Painting an individual hex,
resetting the preview, and importing a fully validated 19-cell JSON
document are each **one atomic and reversible local edit**. No-op paints,
resets and identical imports do not push history or clear a redo branch.
An invalid JSON import changes neither the map nor the edit history.
Performing a real edit after undo discards the previous redo branch.

The pure, immutable `terrainHistory.ts` module keeps at most **32 past
and 32 redo snapshots**, compares the effective 19-cell terrain layout,
and clones inputs from imports. No user data, terrain changes, hero
positions or hidden routes are ever stored on a server, in cookies, or
in localStorage as part of this feature. Undo/redo is preview-only and
does not authorize game movement, reveal private information, or affect
the pre-match guest lobby. The rules, map ownership and match replay
remain dependencies of the approved game design (#3/#5/#9).
