#!/usr/bin/env python3
"""Import only allow-listed Shirokuro art from the original ZIP into lightweight WebP.

Requires Pillow (scripts/requirements-art.txt). The input ZIP is never extracted
to disk. No hidden gameplay data or rules are derived from the artwork.
"""

from __future__ import annotations

import argparse
import io
import json
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

from PIL import Image, ImageOps, UnidentifiedImageError

REPOSITORY = Path(__file__).resolve().parents[1]
HERO_SOURCE_OVERRIDES = {"gordunhao": "Gorduhano", "magjistar": "Magjister"}
TILES = ("black", "white", "city", "sand", "water", "wild")
# The supplied original ZIP contains a legitimate ~23.9MB ancillary asset.
# This bound applies to metadata for every entry, including unused ones.
MAX_ENTRY_BYTES = 32 * 1024 * 1024
MAX_TOTAL_BYTES = 1024 * 1024 * 1024
MAX_ARCHIVE_MEMBERS = 2000
Image.MAX_IMAGE_PIXELS = 20_000_000


def source_images(roster: dict) -> dict[str, tuple[str, tuple[int, int]]]:
    """Explicit source allow-list, with canonical names mapped to legacy file spelling."""
    result: dict[str, tuple[str, tuple[int, int]]] = {}
    seen: set[str] = set()
    for hero in roster["heroes"]:
        hero_id = hero["id"]
        if not isinstance(hero_id, str) or not hero_id or not all(
            part.isalpha() and part.isascii() and part.islower()
            for part in hero_id.split("-")
        ):
            raise ValueError(f"Invalid canonical hero ID: {hero_id!r}")
        if hero_id in seen:
            raise ValueError(f"Duplicate hero ID: {hero_id}")
        seen.add(hero_id)
        name = HERO_SOURCE_OVERRIDES.get(hero_id, hero["name"])
        result[f"heroes/{hero_id}.webp"] = (f"hero base/{name}.png", (480, 560))
    for tile in TILES:
        result[f"tiles/{tile}.webp"] = (f"tasselli/{tile}.png", (448, 448))
    return result


def validate_archive(archive: zipfile.ZipFile) -> dict[str, zipfile.ZipInfo]:
    """Reject malicious paths, repeated ZIP entries and unreasonably large input."""
    members = archive.infolist()
    if len(members) > MAX_ARCHIVE_MEMBERS:
        raise ValueError("Too many entries in source ZIP")
    indexed: dict[str, zipfile.ZipInfo] = {}
    total_size = 0
    for entry in members:
        path = PurePosixPath(entry.filename)
        if (
            path.is_absolute()
            or ".." in path.parts
            or "\\" in entry.filename
            or any(":" in part for part in path.parts)
        ):
            raise ValueError(f"Unsafe ZIP entry name: {entry.filename!r}")
        if entry.filename in indexed:
            raise ValueError(f"Duplicate ZIP entry: {entry.filename!r}")
        if entry.flag_bits & 0x1:
            raise ValueError(f"Encrypted ZIP entry is not supported: {entry.filename!r}")
        indexed[entry.filename] = entry
        total_size += entry.file_size
        if entry.file_size > MAX_ENTRY_BYTES or total_size > MAX_TOTAL_BYTES:
            raise ValueError("Source ZIP exceeds size policy")
    return indexed


def render_webp(archive: zipfile.ZipFile, indexed: dict[str, zipfile.ZipInfo],
                source: str, size: tuple[int, int]) -> bytes:
    info = indexed.get(source)
    if info is None or info.file_size == 0:
        raise ValueError(f"Required artwork is missing or empty: {source}")
    try:
        with Image.open(io.BytesIO(archive.read(info))) as original:
            if original.format != "PNG":
                raise ValueError(f"Expected PNG artwork: {source}")
            original.load()
            rgba = ImageOps.contain(original.convert("RGBA"), size, Image.Resampling.LANCZOS)
            out = io.BytesIO()
            rgba.save(out, format="WEBP", quality=78, method=6)
            return out.getvalue()
    except (UnidentifiedImageError, OSError, Image.DecompressionBombError) as error:
        raise ValueError(f"Invalid artwork {source}: {error}") from error


def import_art(archive_path: Path, output_root: Path, manifest_path: Path,
               roster_path: Path | None = None) -> dict:
    roster_path = roster_path or (REPOSITORY / "src/shared/heroes.json")
    roster = json.loads(roster_path.read_text(encoding="utf-8"))
    mapping = source_images(roster)
    output_root = output_root.resolve()
    manifest_path = manifest_path.resolve()

    # Validate and transform all required files before touching existing output.
    with zipfile.ZipFile(archive_path) as archive:
        indexed = validate_archive(archive)
        rendered = {
            filename: render_webp(archive, indexed, source, dimensions)
            for filename, (source, dimensions) in mapping.items()
        }

    manifest = {
        "schemaVersion": 1,
        "heroes": [hero["id"] for hero in roster["heroes"]],
        "tiles": list(TILES),
    }
    output_root.mkdir(parents=True, exist_ok=True)
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="shiro-art-") as temporary:
        stage = Path(temporary)
        for filename, data in rendered.items():
            destination = stage / filename
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        for filename in rendered:
            destination = output_root / filename
            destination.parent.mkdir(parents=True, exist_ok=True)
            (stage / filename).replace(destination)
        manifest_tmp = stage / "art-manifest.json"
        manifest_tmp.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        manifest_tmp.replace(manifest_path)

    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path, help="Path to the original Shirokuro graphics ZIP")
    parser.add_argument(
        "--output-root", type=Path,
        default=REPOSITORY / "src/frontend/public/art",
        help="WebP destination folder",
    )
    parser.add_argument(
        "--manifest", type=Path,
        default=REPOSITORY / "src/frontend/src/generated/art-manifest.json",
        help="Generated asset availability manifest",
    )
    args = parser.parse_args()
    result = import_art(args.zip, args.output_root, args.manifest)
    print(f"Imported {len(result['heroes'])} hero portraits and {len(result['tiles'])} terrain textures.")


if __name__ == "__main__":
    main()


