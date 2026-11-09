"""Safe card-atlas import tests. CI uses synthetic input instead of original 312MB ZIP."""

import importlib.util
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
SPEC = importlib.util.spec_from_file_location(
    "hero_card_atlas", ROOT / "scripts/build_hero_card_atlas.py")
assert SPEC and SPEC.loader
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class HeroCardAtlasTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.heroes = json.loads((ROOT / "src/shared/heroes.json").read_text())["heroes"]

    def archive(self, missing=None, corrupt=None, extras=(), repeated=None):
        path = self.root / "original.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for face_index, face in enumerate(("front", "back")):
                for i, hero in enumerate(self.heroes):
                    source = module.source_file(hero["id"], face)
                    if source == missing:
                        continue
                    image = Image.new(
                        "RGBA", (72, 28),
                        ((i * 17 + face_index * 41) % 255,
                         (i * 43 + face_index * 23) % 255,
                         (i * 67 + face_index * 53) % 255, 255),
                    )
                    stream = io.BytesIO()
                    image.save(stream, format="PNG")
                    archive.writestr(source,
                                     b"corrupt" if source == corrupt else stream.getvalue())
            for extra in extras:
                archive.writestr(extra, b"malicious")
            if repeated is not None:
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    archive.writestr(repeated, b"duplicate")
        return path

    def test_source_path_covers_28_front_and_back_pngs_in_order(self):
        self.assertEqual(14, len(self.heroes))
        paths = [
            module.source_file(hero["id"], face)
            for face in ("front", "back") for hero in self.heroes
        ]
        self.assertEqual(28, len(paths))
        self.assertEqual(28, len(set(paths)))
        self.assertEqual("screens2/jia oshi.png", module.source_file("jia-oshi", "front"))
        self.assertEqual("screens2/back vlko dlak.png",
                         module.source_file("vlko-dlak", "back"))

    def test_generates_all_28_distinct_cells_with_expected_image_size(self):
        output = self.root / "cards.avif"
        module.build_hero_card_atlas(self.archive(), output)
        self.assertLess(output.stat().st_size, 30_000)
        with Image.open(output) as image:
            self.assertEqual("AVIF", image.format)
            self.assertEqual((1280, 875), image.size)
            pixels = image.convert("RGB")
            sample = {
                pixels.getpixel((
                    (i % 4) * 320 + 160, (i // 4) * 125 + 62
                )) for i in range(28)
            }
            self.assertGreaterEqual(len(sample), 20)

    def test_missing_front_and_reverse_fail_before_writing_output(self):
        for missing in ("screens2/kira.png", "screens2/back krugg.png"):
            output = self.root / "cards.avif"
            with self.assertRaisesRegex(ValueError, "missing or empty"):
                module.build_hero_card_atlas(self.archive(missing=missing), output)
            self.assertFalse(output.exists())

    def test_corrupt_png_rejected(self):
        output = self.root / "cards.avif"
        with self.assertRaisesRegex(ValueError, "Invalid hero sheet"):
            module.build_hero_card_atlas(
                self.archive(corrupt="screens2/back kira.png"), output)
        self.assertFalse(output.exists())

    def test_path_traversal_rejected_even_if_not_used(self):
        output = self.root / "cards.avif"
        with self.assertRaisesRegex(ValueError, "Unsafe ZIP entry"):
            module.build_hero_card_atlas(self.archive(extras=["../../escape"]), output)
        self.assertFalse(output.exists())

    def test_duplicate_archive_filename_is_rejected(self):
        output = self.root / "cards.avif"
        with self.assertRaisesRegex(ValueError, "Duplicate ZIP entry"):
            module.build_hero_card_atlas(
                self.archive(repeated="screens2/kira.png"), output)
        self.assertFalse(output.exists())

    def test_unknown_card_face_and_invalid_id_rejected(self):
        with self.assertRaises(ValueError):
            module.source_file("kira", "other")
        with self.assertRaises(ValueError):
            module.source_file("../kira", "front")


if __name__ == "__main__":
    unittest.main()

