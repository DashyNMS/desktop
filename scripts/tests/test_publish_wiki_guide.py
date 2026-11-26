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
        return wiki.convert(self.guide, "example/desktop", "main")

    def test_nested_links_assets_and_sidebar_sections(self):
        output, errors = self.convert()
        self.assertEqual([], errors)
        self.assertIn("[Help](Help#connection)", output["Find-a-device.md"])
        self.assertIn("[Find](Find-a-device)", output["Help.md"])
        self.assertIn("https://raw.githubusercontent.com/example/desktop/main/docs/screen.png", output["Find-a-device.md"])
        self.assertLess(output["_Sidebar.md"].index("**Tasks**"), output["_Sidebar.md"].index("[Find a device]"))
        self.assertLess(output["_Sidebar.md"].index("**Help**"), output["_Sidebar.md"].index("- [Help]"))

    def test_reserved_names_are_rejected(self):
        for title in ("_Sidebar", "_Footer"):
            self.write("how-to/find.md", f"# {title}\n")
            with self.assertRaisesRegex(SystemExit, "reserved"):
                self.convert()

    def test_root_links_resolve_for_pages_assets_and_sidebar(self):
        self.write("README.md", "# Guide\n\n[Find](/docs/guide/how-to/find.md) [Help](/docs/guide/reference/help.md)\n")
        self.write("how-to/find.md", "# Find a device\n\n[Help](/docs/guide/reference/help.md#connection) ![Screen](/docs/screen.png)\n")
        output, errors = self.convert()
        self.assertEqual([], errors)
        self.assertIn("[Help](Help#connection)", output["Find-a-device.md"])
        self.assertIn("- [Find a device](Find-a-device)", output["_Sidebar.md"])
        self.assertIn("/main/docs/screen.png", output["Find-a-device.md"])

    def test_a_links_title_is_kept_and_its_target_still_checked(self):
        self.write("how-to/find.md", '# Find a device\n\n[Help](../reference/help.md#connection "Connection help") [Gone](gone.md "Gone")\n')
        output, errors = self.convert()
        self.assertIn('[Help](Help#connection "Connection help")', output["Find-a-device.md"])
        self.assertTrue(any("gone.md" in error for error in errors))

    def test_a_block_is_closed_only_by_its_own_kind_of_fence(self):
        self.write("how-to/find.md", "# Find a device\n\n````\n```\n[In code](nowhere.md)\n````\n\n[Missing](missing.md)\n")
        _, errors = self.convert()
        self.assertEqual(1, len(errors))
        self.assertIn("missing.md", errors[0])

    def test_a_link_shown_as_code_is_left_alone_and_code_in_a_link_is_not(self):
        self.write("how-to/find.md", "# Find a device\n\nWrite `[Example](missing.md)` or see [`help`](../reference/help.md).\n")
        output, errors = self.convert()
        self.assertEqual([], errors)
        self.assertIn("`[Example](missing.md)` or see [`help`](Help)", output["Find-a-device.md"])

    def test_escaped_backticks_are_not_code(self):
        self.write("how-to/find.md", "# Find a device\n\n\\`[Help](../reference/help.md)\\` and \\`[Gone](gone.md)\\`\n")
        output, errors = self.convert()
        self.assertIn("[Help](Help)", output["Find-a-device.md"])
        self.assertEqual(1, len(errors))

    def test_a_heading_that_repeats_the_title_is_rejected(self):
        self.write("how-to/find.md", "# Find a device\n\n## Find a device\n")
        _, errors = self.convert()
        self.assertTrue(any("repeats the page's title" in error for error in errors))

    def test_a_repeated_heading_has_its_own_anchor(self):
        self.write("reference/help.md", "# Help\n\n## Connection\n\n## Android\n\n## Android\n")
        self.write("how-to/find.md", "# Find a device\n\n[One](../reference/help.md#android) [Two](../reference/help.md#android-1)\n")
        _, errors = self.convert()
        self.assertEqual([], errors)
        self.write("reference/help.md", "# Help\n\n## Connection\n\n## Android\n\n## Android\n\n## Android-1\n")
        self.write("how-to/find.md", "# Find a device\n\n[Three](../reference/help.md#android-1-1)\n")
        _, errors = self.convert()
        self.assertEqual([], errors)
        self.write("how-to/find.md", "# Find a device\n\n[Three](../reference/help.md#android-2)\n")
        _, errors = self.convert()
        self.assertEqual(1, len(errors))

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
