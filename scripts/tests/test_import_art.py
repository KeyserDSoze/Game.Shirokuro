"""Tests for the safe allow-listed artwork import path (synthetic ZIP fixtures)."""

import importlib.util
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("shiro_art_importer", ROOT / "scripts/import_art.py")
assert SPEC and SPEC.loader
importer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(importer)


class ArtImportTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory()
        self.addCleanup(self.workspace.cleanup)
        self.temp = Path(self.workspace.name)
        self.roster = ROOT / "src/shared/heroes.json"
        self.sources = importer.source_images(json.loads(self.roster.read_text(encoding="utf-8")))
        bitmap = Image.new("RGBA", (16, 20), (100, 130, 200, 255))
        data = io.BytesIO()
        bitmap.save(data, format="PNG")
        self.png = data.getvalue()

    def make_zip(self, skipped=None, extras=None, broken=None):
        archive = self.temp / "input.zip"
        with zipfile.ZipFile(archive, "w") as output:
            for source, _ in self.sources.values():
                if source != skipped:
                    output.writestr(source, b"bad-png" if source == broken else self.png)
            for name in extras or ():
                output.writestr(name, "bad")
        return archive

    def test_roster_maps_all_fourteen_hero_files_and_six_tiles(self):
        self.assertEqual(20, len(self.sources))
        self.assertEqual("hero base/Gorduhano.png", self.sources["heroes/gordunhao.webp"][0])
        self.assertEqual("hero base/Magjister.png", self.sources["heroes/magjistar.webp"][0])
        self.assertIn("tiles/water.webp", self.sources)

    def test_converts_valid_source_and_writes_consistent_manifest(self):
        archive = self.make_zip()
        destination = self.temp / "web"
        manifest = self.temp / "manifest.json"
        data = importer.import_art(archive, destination, manifest)
        self.assertEqual(14, len(data["heroes"]))
        self.assertEqual(6, len(data["tiles"]))
        self.assertEqual(data, json.loads(manifest.read_text(encoding="utf-8")))
        for filename in self.sources:
            with Image.open(destination / filename) as rendered:
                self.assertEqual("WEBP", rendered.format)
                self.assertGreater(rendered.width, 0)
                self.assertLessEqual(rendered.width, 480)

    def test_missing_required_art_fails_without_updating_manifest(self):
        archive = self.make_zip(skipped="hero base/Kira.png")
        manifest = self.temp / "manifest.json"
        with self.assertRaisesRegex(ValueError, "missing or empty"):
            importer.import_art(archive, self.temp / "web", manifest)
        self.assertFalse(manifest.exists())

    def test_corrupt_image_is_rejected(self):
        archive = self.make_zip(broken="tasselli/water.png")
        with self.assertRaisesRegex(ValueError, "Invalid artwork"):
            importer.import_art(archive, self.temp / "web", self.temp / "manifest.json")

    def test_zip_path_traversal_is_rejected_even_for_unused_files(self):
        archive = self.make_zip(extras=["../../bad.txt"])
        with self.assertRaisesRegex(ValueError, "Unsafe ZIP entry"):
            importer.import_art(archive, self.temp / "web", self.temp / "manifest.json")

    def test_duplicate_zip_names_are_rejected(self):
        archive = self.make_zip()
        with zipfile.ZipFile(archive, "a") as z:
            z.writestr("hero base/Kira.png", self.png)
        with self.assertRaisesRegex(ValueError, "Duplicate ZIP entry"):
            importer.import_art(archive, self.temp / "web", self.temp / "manifest.json")


    def test_large_unused_archive_member_is_allowed_within_32_mib_limit(self):
        archive = self.make_zip()
        # The supplied ZIP contains a valid unused entry larger than 16 MiB.
        # Store as compressed zeroes for a small, fast CI fixture.
        with zipfile.ZipFile(archive, "a", compression=zipfile.ZIP_DEFLATED) as z:
            z.writestr("unused-large-reference.bin", bytes(17 * 1024 * 1024))
        with zipfile.ZipFile(archive) as z:
            indexed = importer.validate_archive(z)
        self.assertEqual(17 * 1024 * 1024, indexed["unused-large-reference.bin"].file_size)

    def test_large_entries_above_the_configured_limit_are_still_rejected(self):
        archive = self.make_zip()
        with zipfile.ZipFile(archive, "a") as z:
            z.writestr("oversized-unused.bin", bytes(2049))
        from unittest.mock import patch
        with patch.object(importer, "MAX_ENTRY_BYTES", 2048):
            with zipfile.ZipFile(archive) as z:
                with self.assertRaisesRegex(ValueError, "exceeds size policy"):
                    importer.validate_archive(z)

    def test_zero_byte_source_is_rejected(self):
        archive = self.make_zip(skipped="hero base/Kira.png")
        with zipfile.ZipFile(archive, "a") as z:
            z.writestr("hero base/Kira.png", b"")
        with self.assertRaisesRegex(ValueError, "missing or empty"):
            importer.import_art(archive, self.temp / "web", self.temp / "manifest.json")


if __name__ == "__main__":
    unittest.main()


