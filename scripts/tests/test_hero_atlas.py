"""Regression tests for a compact art atlas using synthetic PNG sources."""

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
SPEC = importlib.util.spec_from_file_location("shiro_hero_atlas", ROOT / "scripts/build_hero_atlas.py")
assert SPEC and SPEC.loader
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class AtlasTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.heroNames = [
            "Adsiz", "Bazmut", "Gorduhano", "Howler", "Jia Oshi", "Kira", "Krugg",
            "Kylia", "La Morte", "Magjister", "Sandgespenst", "Sangar Tedra", "Shilla", "Vlko Dlak",
        ]

    def archive(self, missing=None, invalid=None):
        path = self.root / "input.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for i, name in enumerate(self.heroNames):
                file = f"hero base/{name}.png"
                if file == missing:
                    continue
                image = Image.new("RGBA", (24, 28), (i * 15, 220 - i * 12, i * 8, 255))
                stream = io.BytesIO()
                image.save(stream, format="PNG")
                archive.writestr(file, b"bad" if file == invalid else stream.getvalue())
        return path

    def test_generates_all_fourteen_art_cells_in_canonical_order(self):
        output = self.root / "atlas.webp"
        module.build_hero_atlas(self.archive(), output)
        with Image.open(output) as image:
            self.assertEqual("WEBP", image.format)
            self.assertEqual((448, 148), image.size)
            samples = [
                image.getpixel((64 * (i % 7) + 32, 74 * (i // 7) + 45))
                for i in range(14)
            ]
            self.assertEqual(14, len(set(samples)))

    def test_missing_art_cannot_produce_incomplete_atlas(self):
        output = self.root / "atlas.webp"
        with self.assertRaisesRegex(ValueError, "Required artwork missing"):
            module.build_hero_atlas(self.archive(missing="hero base/Kira.png"), output)
        self.assertFalse(output.exists())

    def test_invalid_image_is_not_accepted(self):
        output = self.root / "atlas.webp"
        with self.assertRaises((OSError, ValueError)):
            module.build_hero_atlas(self.archive(invalid="hero base/Kira.png"), output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()

