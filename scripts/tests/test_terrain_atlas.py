"""Tests with synthetic tile PNGs; original art need not be in the public repository."""

import importlib.util
import io
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
SPEC = importlib.util.spec_from_file_location("shiro_terrain_atlas", ROOT / "scripts/build_terrain_atlas.py")
assert SPEC and SPEC.loader
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class TerrainAtlasTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def archive(self, missing=None, broken=None, extras=()):
        path = self.root / "tiles.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for index, name in enumerate(module.TILES):
                source = f"tasselli/{name}.png"
                if source == missing:
                    continue
                tile = Image.new("RGBA", (24, 30), (10 + index*36, 20 + index*30, 180-index*20, 255))
                buffer = io.BytesIO()
                tile.save(buffer, format="PNG")
                archive.writestr(source, b"not a png" if source == broken else buffer.getvalue())
            for entry in extras:
                archive.writestr(entry, b"unsafe")
        return path

    def test_six_named_tiles_are_in_fixed_row_major_order(self):
        self.assertEqual(("black", "white", "city", "sand", "water", "wild"), module.TILES)
        output = self.root / "atlas.webp"
        module.build_terrain_atlas(self.archive(), output)
        with Image.open(output) as image:
            self.assertEqual("WEBP", image.format)
            self.assertEqual((192, 128), image.size)
            samples = [
                image.getpixel((index % 3 * 64 + 32, index // 3 * 64 + 32))
                for index in range(6)
            ]
            self.assertEqual(6, len(set(samples)))

    def test_missing_tile_rejects_incomplete_output(self):
        output = self.root / "atlas.webp"
        with self.assertRaisesRegex(ValueError, "Missing or empty"):
            module.build_terrain_atlas(self.archive(missing="tasselli/water.png"), output)
        self.assertFalse(output.exists())

    def test_corrupt_tile_rejects_incomplete_output(self):
        output = self.root / "atlas.webp"
        with self.assertRaisesRegex(ValueError, "Invalid tile"):
            module.build_terrain_atlas(self.archive(broken="tasselli/sand.png"), output)
        self.assertFalse(output.exists())

    def test_zip_traversal_is_rejected_even_in_unused_entry(self):
        output = self.root / "atlas.webp"
        with self.assertRaisesRegex(ValueError, "Unsafe ZIP entry"):
            module.build_terrain_atlas(self.archive(extras=["../../bad"]), output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()

