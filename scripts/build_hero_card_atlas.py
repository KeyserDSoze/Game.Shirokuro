#!/usr/bin/env python3
"""Convert the original fourteen screens2 front/reverse hero sheets to one AVIF atlas.

This is preview artwork only. Text in the originals remains unvalidated and
must not be treated as gameplay rules or accessible digital text.
"""

from __future__ import annotations

import argparse
import io
import json
import zipfile
from pathlib import Path

from PIL import Image, ImageOps, UnidentifiedImageError

from import_art import REPOSITORY, validate_archive

COLUMNS = 4
CARD_WIDTH = 320
CARD_HEIGHT = 125
EXPECTED_HEROES = 14
BACKGROUND = "#f0efe8"
Image.MAX_IMAGE_PIXELS = 20_000_000


def source_file(hero_id: str, face: str) -> str:
    if face not in ("front", "back"):
        raise ValueError("Unknown hero-card face")
    if not hero_id or not all(part.isascii() and part.islower() and part.isalpha()
                              for part in hero_id.split("-")):
        raise ValueError("Invalid canonical hero ID")
    return f"screens2/{'back ' if face == 'back' else ''}{hero_id.replace('-', ' ')}.png"


def build_hero_card_atlas(
    archive_path: Path,
    output_path: Path,
    roster_path: Path | None = None,
) -> None:
    roster_path = roster_path or REPOSITORY / "src/shared/heroes.json"
    roster = json.loads(roster_path.read_text(encoding="utf-8"))
    if roster.get("schemaVersion") != 1:
        raise ValueError("Unsupported hero roster schema")
    heroes = roster["heroes"]
    if len(heroes) != EXPECTED_HEROES:
        raise ValueError("Expected exactly 14 original heroes")
    ids = [hero["id"] for hero in heroes]
    if len(set(ids)) != EXPECTED_HEROES:
        raise ValueError("Repeated hero ID")

    atlas = Image.new("RGB", (CARD_WIDTH * COLUMNS, CARD_HEIGHT * 7), BACKGROUND)
    with zipfile.ZipFile(archive_path) as archive:
        indexed = validate_archive(archive)
        for face_index, face in enumerate(("front", "back")):
            for i, hero_id in enumerate(ids):
                source = source_file(hero_id, face)
                entry = indexed.get(source)
                if entry is None or entry.file_size == 0:
                    raise ValueError(f"Required hero sheet missing or empty: {source}")

                try:
                    with Image.open(io.BytesIO(archive.read(entry))) as original:
                        if original.format != "PNG":
                            raise ValueError(f"Expected PNG reference sheet: {source}")
                        original.load()
                        sized = ImageOps.contain(
                            original.convert("RGBA"), (CARD_WIDTH, CARD_HEIGHT),
                            Image.Resampling.LANCZOS,
                        )
                        canvas = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), BACKGROUND)
                        canvas.alpha_composite(
                            sized, ((CARD_WIDTH - sized.width) // 2,
                                    (CARD_HEIGHT - sized.height) // 2),
                        )
                        index = face_index * EXPECTED_HEROES + i
                        atlas.paste(
                            canvas.convert("RGB"),
                            ((index % COLUMNS) * CARD_WIDTH,
                             (index // COLUMNS) * CARD_HEIGHT),
                        )
                except (OSError, UnidentifiedImageError, Image.DecompressionBombError) as error:
                    raise ValueError(f"Invalid hero sheet: {source}") from error

    output_path.parent.mkdir(parents=True, exist_ok=True)
    atlas.save(output_path, "AVIF", quality=7, speed=6)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path, help="Original Shirokuro artwork ZIP")
    parser.add_argument("--output", type=Path, default=(
        REPOSITORY / "src/frontend/public/art/hero-ability-cards-original.avif"))
    args = parser.parse_args()
    build_hero_card_atlas(args.zip, args.output)
    print(f"Wrote 28 original sheet thumbnails to {args.output}")


if __name__ == "__main__":
    main()

