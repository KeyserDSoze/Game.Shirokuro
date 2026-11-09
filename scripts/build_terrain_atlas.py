#!/usr/bin/env python3
"""Build a small 3x2 WebP reference atlas from the user's six original tasselli PNG files."""

from __future__ import annotations

import argparse
import io
import zipfile
from pathlib import Path

from PIL import Image, ImageOps, UnidentifiedImageError
from import_art import REPOSITORY, TILES, validate_archive

COLUMNS = 3
CELL_SIZE = 64
BACKGROUND = {
    "black": "#2c2d30", "white": "#f6f5ed", "city": "#a2c6bb",
    "sand": "#e3c47f", "water": "#467c9a", "wild": "#42783a",
}


def build_terrain_atlas(archive_path: Path, output_path: Path) -> None:
    if len(TILES) != 6:
        raise ValueError("Unexpected official artwork tile inventory")
    atlas = Image.new("RGB", (COLUMNS * CELL_SIZE, 2 * CELL_SIZE), "#252938")

    with zipfile.ZipFile(archive_path) as archive:
        indexed = validate_archive(archive)
        for index, name in enumerate(TILES):
            source = f"tasselli/{name}.png"
            info = indexed.get(source)
            if info is None or info.file_size == 0:
                raise ValueError(f"Missing or empty tile artwork: {source}")
            try:
                with Image.open(io.BytesIO(archive.read(info))) as original:
                    if original.format != "PNG":
                        raise ValueError(f"Expected PNG artwork: {source}")
                    original.load()
                    tile = ImageOps.contain(original.convert("RGBA"), (CELL_SIZE, CELL_SIZE),
                                            Image.Resampling.LANCZOS)
                    layer = Image.new("RGBA", (CELL_SIZE, CELL_SIZE), BACKGROUND[name])
                    layer.alpha_composite(tile, ((CELL_SIZE - tile.width)//2, (CELL_SIZE - tile.height)//2))
                    atlas.paste(layer.convert("RGB"),
                                ((index % COLUMNS) * CELL_SIZE, (index // COLUMNS) * CELL_SIZE))
            except (UnidentifiedImageError, OSError, Image.DecompressionBombError) as error:
                raise ValueError(f"Invalid tile artwork: {source}") from error

    output_path.parent.mkdir(parents=True, exist_ok=True)
    atlas.save(output_path, "WEBP", quality=43, method=6)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path, help="Original Shirokuro graphics ZIP")
    parser.add_argument(
        "--output", type=Path,
        default=REPOSITORY / "src/frontend/public/art/terrain-original-atlas.webp",
    )
    args = parser.parse_args()
    build_terrain_atlas(args.zip, args.output)
    print(f"Generated {args.output}")


if __name__ == "__main__":
    main()

