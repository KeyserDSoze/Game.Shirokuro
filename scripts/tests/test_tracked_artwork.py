"""Verify that the actual small reference assets tracked on main are valid and complete.

Other importer tests generate synthetic PNGs; these checks guard against
accidentally deleting, truncating or mismapping committed binary reference artwork files.
"""

import unittest
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
ART = ROOT / "src/frontend/public/art"


class TrackedArtworkTests(unittest.TestCase):
    def check_reference_atlas(self, filename, dimensions, tile_width, tile_height, columns, count):
        path = ART / filename
        self.assertTrue(path.is_file(), f"Missing committed reference atlas: {path}")
        self.assertLess(path.stat().st_size, 30_000)
        with Image.open(path) as art:
            self.assertEqual("WEBP", art.format)
            self.assertEqual(dimensions, art.size)
            self.assertEqual("RGB", art.convert("RGB").mode)
            pixels = art.convert("RGB")
            centers = [
                pixels.getpixel((
                    (index % columns) * tile_width + tile_width // 2,
                    (index // columns) * tile_height + tile_height // 2,
                ))
                for index in range(count)
            ]
            self.assertEqual(count, len(set(centers)), "All artwork cells should show distinct reference art")


    def test_original_ability_cards_atlas_has_all_28_sheets(self):
        import hashlib

        path = ART / "hero-ability-cards-original.avif"
        self.assertTrue(path.is_file(), f"Missing tracked ability card atlas: {path}")
        self.assertLess(path.stat().st_size, 30_000)
        with Image.open(path) as artwork:
            self.assertEqual("AVIF", artwork.format)
            self.assertEqual((1280, 875), artwork.size)
            rgb = artwork.convert("RGB")
            # A unique cell per face/hero catches clipped and repeated sheets.
            fingerprints = set()
            for index in range(28):
                x = (index % 4) * 320
                y = (index // 4) * 125
                fingerprints.add(hashlib.sha256(
                    rgb.crop((x, y, x + 320, y + 125)).tobytes()).digest())
            self.assertEqual(28, len(fingerprints))


    def test_original_movement_cards_atlas_has_130_distinct_cells(self):
        import hashlib

        path = ART / "movement-cards-original.avif"
        self.assertTrue(path.is_file(), f"Missing committed movement card atlas: {path}")
        self.assertLess(path.stat().st_size, 30_000)
        with Image.open(path) as artwork:
            self.assertEqual("AVIF", artwork.format)
            self.assertEqual((560, 1196), artwork.size)
            rgb = artwork.convert("RGB")
            fingerprints = set()
            for index in range(130):
                x = (index % 10) * 56
                y = (index // 10) * 92
                fingerprints.add(hashlib.sha256(
                    rgb.crop((x, y, x + 56, y + 92)).tobytes()).digest())
            self.assertEqual(130, len(fingerprints))

    def test_original_hero_reference_has_all_fourteen_cells(self):
        self.check_reference_atlas(
            "heroes-original-atlas.webp", (448, 148), 64, 74, 7, 14)

    def test_original_terrain_reference_has_all_six_cells(self):
        self.check_reference_atlas(
            "terrain-original-atlas.webp", (192, 128), 64, 64, 3, 6)


if __name__ == "__main__":
    unittest.main()



