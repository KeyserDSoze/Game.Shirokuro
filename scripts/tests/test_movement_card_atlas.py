"""Synthetic ZIP regressions for the original Shirokuro movement-card importer."""

import io
import json
import sys
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import build_movement_card_atlas as movement


class MovementCardsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.roster = json.loads((ROOT / "src/shared/heroes.json").read_text())
        self.available = [h for h in self.roster["heroes"] if h["id"] != movement.MISSING_HERO_ID]
        self.roster_path = self.root / "heroes.json"
        self.roster_path.write_text(json.dumps(self.roster))

    def archive(self, *, missing=None, broken=None, extras=(), repeated=None):
        path = self.root / "source.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for hero_index, hero in enumerate(self.available):
                for number in range(1, 11):
                    source = movement.movement_source(hero["id"], hero["name"], number)
                    if source == missing:
                        continue
                    image = Image.new("RGBA", (30, 44), (
                        (hero_index * 18 + number * 7) % 256,
                        (hero_index * 12 + number * 20) % 256,
                        (hero_index * 31 + number * 9) % 256, 255,
                    ))
                    buffer = io.BytesIO()
                    image.save(buffer, format="PNG")
                    archive.writestr(source, b"not an image" if source == broken else buffer.getvalue())
            for entry in extras:
                archive.writestr(entry, b"bad")
            if repeated:
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    archive.writestr(repeated, b"bad")
        return path

    def build(self, archive_path):
        output = self.root / "out.avif"
        movement.build_movement_card_atlas(archive_path, output, self.roster_path)
        return output

    def test_canonical_folders_numbers_and_absent_source(self):
        self.assertEqual(13, len(self.available))
        self.assertEqual("carte movimento3/La Morte/10.png",
                         movement.movement_source("la-morte", "La Morte", 10))
        self.assertEqual("carte movimento3/Magjistar/1.png",
                         movement.movement_source("magjistar", "Magjistar", 1))
        with self.assertRaises(ValueError):
            movement.movement_source("gordunhao", "Gordunhao", 1)
        for number in (0, 11, 1.5, True):
            with self.assertRaises(ValueError):
                movement.movement_source("kira", "Kira", number)

    def test_generates_130_distinct_original_reference_cells(self):
        import hashlib

        output = self.build(self.archive())
        self.assertLess(output.stat().st_size, 30000)
        with Image.open(output) as img:
            self.assertEqual("AVIF", img.format)
            self.assertEqual((560, 1196), img.size)
            rgb = img.convert("RGB")
            fingerprints = {
                hashlib.sha256(rgb.crop((
                    (index % 10) * 56, (index // 10) * 92,
                    ((index % 10) + 1) * 56, ((index // 10) + 1) * 92,
                )).tobytes()).digest()
                for index in range(130)
            }
            self.assertEqual(130, len(fingerprints))

    def test_missing_card_rejected_without_partial_output(self):
        output = self.root / "out.avif"
        with self.assertRaisesRegex(ValueError, "Missing or empty"):
            self.build(self.archive(missing="carte movimento3/Kira/10.png"))
        self.assertFalse(output.exists())

    def test_corrupt_png_rejected(self):
        with self.assertRaisesRegex(ValueError, "Invalid original"):
            self.build(self.archive(broken="carte movimento3/Kira/1.png"))

    def test_zip_traversal_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unsafe ZIP"):
            self.build(self.archive(extras=["../escape"]))

    def test_duplicate_zip_entry_rejected(self):
        with self.assertRaisesRegex(ValueError, "Duplicate ZIP"):
            self.build(self.archive(repeated="carte movimento3/Kira/1.png"))

    def test_new_gordunhao_data_forces_catalog_revisit(self):
        with self.assertRaisesRegex(ValueError, "inventory has changed"):
            self.build(self.archive(extras=["carte movimento3/Gordunhao/1.png"]))

    def test_duplicate_roster_id_rejected(self):
        self.roster["heroes"][1]["id"] = self.roster["heroes"][0]["id"]
        self.roster_path.write_text(json.dumps(self.roster))
        with self.assertRaisesRegex(ValueError, "Missing/duplicate"):
            self.build(self.archive())


if __name__ == "__main__":
    unittest.main()

