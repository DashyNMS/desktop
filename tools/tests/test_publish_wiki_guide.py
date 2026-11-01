"""Exercise wiki publishing with a nested guide and repository assets."""

import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location(
    "wiki", Path(__file__).resolve().parents[1] / "Publish-WikiGuide.py"
)
wiki = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(wiki)


class NestedGuideTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / ".git").mkdir()
        self.guide = self.root / "docs/guide"
        self.write("README.md", "# Guide\n\n## Tasks\n\n- [Find](how-to/find.md)\n\n## Help\n\n- [Help](reference/help.md)\n")
        self.write("how-to/find.md", "# Find a device\n\n[Help](../reference/help.md#connection)\n\n![Screen](../../screen.png)\n")
        self.write("reference/help.md", "# Help\n\n## Connection\n\n[Find](../how-to/find.md)\n")
        (self.root / "docs/screen.png").touch()

    def write(self, name, text):
        path = self.guide / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def convert(self):
        return wiki.convert(self.guide, "example/desktop", "master")

    def test_nested_links_assets_and_sidebar_sections(self):
        output, errors = self.convert()
        self.assertEqual([], errors)
        self.assertIn("[Help](Help#connection)", output["Find-a-device.md"])
        self.assertIn("[Find](Find-a-device)", output["Help.md"])
        self.assertIn("https://raw.githubusercontent.com/example/desktop/master/docs/screen.png", output["Find-a-device.md"])
        self.assertLess(output["_Sidebar.md"].index("**Tasks**"), output["_Sidebar.md"].index("[Find a device]"))
        self.assertLess(output["_Sidebar.md"].index("**Help**"), output["_Sidebar.md"].index("- [Help]"))

    def test_missing_nested_anchor_is_rejected(self):
        self.write("reference/help.md", "# Help\n")
        _, errors = self.convert()
        self.assertTrue(any("no heading for #connection" in error for error in errors))

    def test_unlisted_nested_page_is_rejected(self):
        self.write("how-to/extra.md", "# Extra task\n")
        _, errors = self.convert()
        self.assertTrue(any("doesn't link to how-to/extra.md" in error for error in errors))

    def test_duplicate_titles_across_folders_are_rejected(self):
        self.write("how-to/extra.md", "# Help\n")
        with self.assertRaisesRegex(SystemExit, "same title"):
            self.convert()

    def test_missing_nested_page_is_rejected(self):
        self.write("how-to/find.md", "# Find a device\n\n[Missing](../reference/missing.md)\n")
        _, errors = self.convert()
        self.assertTrue(any("isn't a file" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
