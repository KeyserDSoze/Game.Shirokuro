#!/usr/bin/env python3
"""Generate a compact 7x2 hero-reference WebP sprite from the original art ZIP.

Requires Pillow from scripts/requirements-art.txt. Source order comes from
src/shared/heroes.json, not ZIP enumeration, to match the frontend atlas map.
"""

from __future__ import annotations

import argparse
import io
import json
import zipfile
from pathlib import Path
from PIL import Image

from import_art import REPOSITORY, source_images, validate_archive

COLUMNS = 7
CELL_WIDTH = 64
CELL_HEIGHT = 74


def build_hero_atlas(archive_path: Path, output_path: Path,
                     roster_path: Path | None = None) -> None:
    roster_path = roster_path or REPOSITORY / "src/shared/heroes.json"
    roster = json.loads(roster_path.read_text(encoding="utf-8"))
    heroes = roster["heroes"]
    if len(heroes) != 14:
        raise ValueError("The reference atlas layout needs the original 14 heroes.")

    sources = source_images(roster)
    atlas = Image.new("RGB", (CELL_WIDTH * COLUMNS, CELL_HEIGHT * 2), "#1b1e29")

    with zipfile.ZipFile(archive_path) as archive:
        indexed = validate_archive(archive)
        for index, hero in enumerate(heroes):
            source = sources[f"heroes/{hero['id']}.webp"][0]
            entry = indexed.get(source)
            if entry is None or entry.file_size == 0:
                raise ValueError(f"Required artwork missing: {source}")
            with Image.open(io.BytesIO(archive.read(entry))) as image:
                if image.format != "PNG":
                    raise ValueError(f"Expected PNG: {source}")
                image.load()
                cut = image.convert("RGBA")
                bounds = cut.getbbox()
                if bounds is None:
                    raise ValueError(f"Empty hero artwork: {source}")
                cut = cut.crop(bounds)
                cut.thumbnail((CELL_WIDTH - 4, CELL_HEIGHT - 4), Image.Resampling.LANCZOS)
                x = (index % COLUMNS) * CELL_WIDTH + (CELL_WIDTH - cut.width) // 2
                y = (index // COLUMNS) * CELL_HEIGHT + CELL_HEIGHT - cut.height
                atlas.paste(cut, (x, y), cut)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    atlas.save(output_path, "WEBP", quality=12, method=6)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path)
    parser.add_argument(
        "--output", type=Path,
        default=REPOSITORY / "src/frontend/public/art/heroes-original-atlas.webp",
    )
    args = parser.parse_args()
    build_hero_atlas(args.zip, args.output)
    print(f"Generated {args.output}")


if __name__ == "__main__":
    main()

