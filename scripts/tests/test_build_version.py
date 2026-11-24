import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "build_version.py"


class BuildVersionTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        self.git("init", "--quiet", "-b", "main")
        (self.root / "Directory.Build.props").write_text("<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>")
        self.notes("## New\n\n- Something to notice.\n")
        self.commit("feat(start): the first commit")

    def notes(self, text, version="2.0.0"):
        path = self.root / f"docs/releases/{version}.md"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def git(self, *args):
        subprocess.run(("git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", *args),
                       cwd=self.root, check=True, capture_output=True)

    def commit(self, subject):
        (self.root / "file").write_text(subject)
        self.git("add", "-A")
        self.git("commit", "--quiet", "-m", subject)

    def run_script(self, *args, event="push", number="7"):
        env = {k: v for k, v in os.environ.items() if k != "GITHUB_OUTPUT"}
        env.update(GITHUB_RUN_NUMBER=number, GITHUB_EVENT_NAME=event, GITHUB_REPOSITORY="DashyNMS/example")
        result = subprocess.run((sys.executable, str(SCRIPT), *args), cwd=self.root, env=env, capture_output=True, text=True)
        self.error = result.stderr
        return dict(line.split("=", 1) for line in result.stdout.splitlines()) if result.returncode == 0 else None

    def test_edge_and_nightly_are_numbered_by_run(self):
        self.assertEqual("2.0.0-edge.7", self.run_script("edge")["version"])
        self.assertEqual("v2.0.0-nightly.8", self.run_script("nightly", number="8")["tag"])

    def test_the_schedule_skips_a_commit_that_already_has_a_nightly(self):
        self.assertEqual("true", self.run_script("nightly", event="schedule")["changed"])
        self.git("tag", "v2.0.0-nightly.3")
        self.assertEqual("false", self.run_script("nightly", event="schedule")["changed"])
        self.assertEqual("true", self.run_script("nightly", event="workflow_dispatch")["changed"])
        self.commit("ci: something")
        self.assertEqual("true", self.run_script("nightly", event="schedule")["changed"])

    def test_a_nightly_is_a_copy_of_the_notes_with_a_link_to_what_changed(self):
        self.git("tag", "v2.0.0-preview.20")
        self.commit("fix: later")
        self.git("tag", "v2.0.0-edge.6")
        self.run_script("nightly", "--notes", "out/notes.md")
        text = (self.root / "out/notes.md").read_text()
        self.assertTrue(text.startswith("## New\n\n- Something to notice.\n"))
        self.assertIn("https://github.com/DashyNMS/example/compare/v2.0.0-preview.20...v2.0.0-nightly.7", text)

    def test_the_link_starts_from_whatever_came_out_last(self):
        self.git("tag", "v2.0.0-nightly.4")
        os.environ["GIT_COMMITTER_DATE"] = "2090-01-01T00:00:00Z"
        self.addCleanup(os.environ.pop, "GIT_COMMITTER_DATE")
        self.commit("feat: in the release")
        self.git("tag", "v1.9.0")
        self.run_script("nightly", "--notes", "out/notes.md")
        self.assertIn("compare/v1.9.0...", (self.root / "out/notes.md").read_text())

    def test_an_edge_build_links_to_the_notes_and_what_changed_since_the_last_one(self):
        self.git("tag", "v2.0.0-nightly.1")
        os.environ["GIT_COMMITTER_DATE"] = "2090-01-01T00:00:00Z"
        self.addCleanup(os.environ.pop, "GIT_COMMITTER_DATE")
        self.commit("fix: later")
        self.git("tag", "v2.0.0-edge.6")
        self.run_script("edge", "--notes", "out/notes.md")
        text = (self.root / "out/notes.md").read_text()
        self.assertIn("compare/v2.0.0-edge.6...v2.0.0-edge.7", text)
        self.assertIn("docs/releases/2.0.0.md", text)
        self.assertNotIn("Something to notice", text)

    def test_an_edge_build_compares_with_the_last_edge_even_after_a_nightly(self):
        self.git("tag", "v2.0.0-edge.6")
        os.environ["GIT_COMMITTER_DATE"] = "2090-01-01T00:00:00Z"
        self.addCleanup(os.environ.pop, "GIT_COMMITTER_DATE")
        self.commit("fix: later")
        self.git("tag", "v2.0.0-nightly.1")
        self.run_script("edge", "--notes", "out/notes.md")
        self.assertIn("compare/v2.0.0-edge.6...v2.0.0-edge.7", (self.root / "out/notes.md").read_text())

    def test_the_first_edge_build_compares_with_the_last_build_of_any_kind(self):
        self.git("tag", "v2.0.0-nightly.1")
        self.run_script("edge", "--notes", "out/notes.md")
        self.assertIn("compare/v2.0.0-nightly.1...v2.0.0-edge.7", (self.root / "out/notes.md").read_text())

    def test_a_release_is_the_notes_themselves(self):
        self.git("tag", "v2.0.0")
        self.run_script("release", "--tag", "v2.0.0", "--notes", "out/notes.md")
        self.assertEqual("## New\n\n- Something to notice.\n", (self.root / "out/notes.md").read_text())

    def test_nothing_is_built_without_notes_for_its_version(self):
        self.assertEqual({}, self.run_script("check"))
        for text in ("", "Just words.\n", "## New\n", "## Improvements\n\n- Wrong heading.\n"):
            self.notes(text)
            for channel in ("check", "edge", "nightly"):
                self.assertIsNone(self.run_script(channel), text)
        (self.root / "docs/releases/2.0.0.md").unlink()
        self.assertIsNone(self.run_script("check"))
        self.assertIn("2.0.0.md is missing", self.error)

    def test_a_release_must_be_the_props_version_on_this_commit(self):
        self.git("tag", "v2.0.0")
        self.git("tag", "v3.0.0")
        self.assertEqual("2.0.0", self.run_script("release", "--tag", "v2.0.0")["version"])
        for tag in ("v3.0.0", "v2.0.0-nightly.1", "v9.9.9"):
            self.assertIsNone(self.run_script("release", "--tag", tag), tag)
        self.commit("fix: later")
        self.assertIsNone(self.run_script("release", "--tag", "v2.0.0"))
        self.assertIn("newest commit", self.error)

    def test_store_build_numbers_are_past_the_ones_already_used(self):
        build = int(self.run_script("edge")["store_build"])
        self.assertGreater(build, 400_000)
        self.assertLess(build, 2_000_000_000)

    def test_a_release_does_not_ship_a_build_of_another_package(self):
        (self.root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>2.0.0</Version><DashyNMSCoreVersion>2.0.0-edge.1</DashyNMSCoreVersion></PropertyGroup></Project>")
        self.commit("build: pin")
        self.git("tag", "v2.0.0")
        self.assertIsNone(self.run_script("release", "--tag", "v2.0.0"))
        self.assertIn("DashyNMSCoreVersion", self.error)
        self.assertIsNotNone(self.run_script("edge"))

    def test_a_prerelease_base_version_is_refused(self):
        (self.root / "Directory.Build.props").write_text("<Project><PropertyGroup><Version>2.0.0-dev</Version></PropertyGroup></Project>")
        self.assertIsNone(self.run_script("edge"))


if __name__ == "__main__":
    unittest.main()
