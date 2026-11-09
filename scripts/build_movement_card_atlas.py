#!/usr/bin/env python3
"""Build one compact AVIF reference atlas of the 130 original movement-card PNGs.

The ZIP contains ten numbered files for 13 heroes. Gordunhao has no cards:
do not fabricate or infer his gameplay mechanics from this omission.
"""

from __future__ import annotations

import argparse
import io
import json
import zipfile
from pathlib import Path

from PIL import Image, ImageOps, UnidentifiedImageError

from import_art import REPOSITORY, validate_archive

CARDS_PER_HERO = 10
ATLAS_COLUMNS = 10
CARD_WIDTH = 56
CARD_HEIGHT = 92
HERO_COUNT = 14
MISSING_HERO_ID = "gordunhao"
BACKGROUND = "#eeece5"


def movement_source(hero_id: str, hero_name: str, number: int) -> str:
    if (not hero_id or not all(part.isascii() and part.isalpha() and part.islower()
                               for part in hero_id.split("-"))):
        raise ValueError("Invalid canonical hero ID")
    if hero_id == MISSING_HERO_ID:
        raise ValueError("Gordunhao movement artwork is absent in the original ZIP")
    if not hero_name or not all(part.isascii() and
                                (part.isalpha() or part == " ")
                                for part in hero_name) or hero_name != hero_name.strip():
        raise ValueError("Unsafe source folder name")
    if type(number) is not int or not 1 <= number <= CARDS_PER_HERO:
        raise ValueError("Movement-card number out of range")
    return f"carte movimento3/{hero_name}/{number}.png"


def build_movement_card_atlas(
    archive_path: Path, output_path: Path, roster_path: Path | None = None,
) -> None:
    roster_path = roster_path or REPOSITORY / "src/shared/heroes.json"
    roster = json.loads(roster_path.read_text(encoding="utf-8"))
    heroes = roster["heroes"]
    if roster.get("schemaVersion") != 1 or len(heroes) != HERO_COUNT:
        raise ValueError("Unsupported original hero roster")
    ids = [hero["id"] for hero in heroes]
    if len(set(ids)) != HERO_COUNT or MISSING_HERO_ID not in ids:
        raise ValueError("Missing/duplicate canonical hero IDs")
    available = [hero for hero in heroes if hero["id"] != MISSING_HERO_ID]

    atlas = Image.new("RGB", (
        CARD_WIDTH * ATLAS_COLUMNS,
        CARD_HEIGHT * len(available),
    ), BACKGROUND)
    with zipfile.ZipFile(archive_path) as archive:
        indexed = validate_archive(archive)
        # Explicitly detect any new Gordunhao originals, which must trigger a
        # catalogue update rather than silently being discarded.
        unexpected = [path for path in indexed if
                      path.startswith("carte movimento3/Gordunhao/") and
                      path.lower().endswith(".png")]
        if unexpected:
            raise ValueError("Gordunhao card inventory has changed; update catalog")

        for row, hero in enumerate(available):
            for card_number in range(1, CARDS_PER_HERO + 1):
                source = movement_source(hero["id"], hero["name"], card_number)
                info = indexed.get(source)
                if info is None or info.file_size == 0:
                    raise ValueError(f"Missing or empty original movement card: {source}")
                try:
                    with Image.open(io.BytesIO(archive.read(info))) as original:
                        if original.format != "PNG":
                            raise ValueError(f"Expected PNG movement card: {source}")
                        original.load()
                        sized = ImageOps.contain(
                            original.convert("RGBA"), (CARD_WIDTH, CARD_HEIGHT),
                            Image.Resampling.LANCZOS,
                        )
                        canvas = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), BACKGROUND)
                        canvas.alpha_composite(sized, (
                            (CARD_WIDTH - sized.width) // 2,
                            (CARD_HEIGHT - sized.height) // 2,
                        ))
                        atlas.paste(canvas.convert("RGB"), (
                            (card_number - 1) * CARD_WIDTH,
                            row * CARD_HEIGHT,
                        ))
                except (UnidentifiedImageError, OSError, Image.DecompressionBombError) as error:
                    raise ValueError(f"Invalid original movement card: {source}") from error

    output_path.parent.mkdir(parents=True, exist_ok=True)
    atlas.save(output_path, format="AVIF", quality=3, speed=6)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path)
    parser.add_argument(
        "--output", type=Path, default=REPOSITORY /
        "src/frontend/public/art/movement-cards-original.avif",
    )
    args = parser.parse_args()
    build_movement_card_atlas(args.zip, args.output)
    print(f"Wrote 130 original movement-card thumbnails to {args.output}")


if __name__ == "__main__":
    main()

