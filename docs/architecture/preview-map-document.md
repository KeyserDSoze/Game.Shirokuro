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

