import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "issue_metadata", Path(__file__).resolve().parents[1] / "issue_metadata.py"
)
metadata = importlib.util.module_from_spec(spec)
spec.loader.exec_module(metadata)


class IssueMetadataTests(unittest.TestCase):
    def setUp(self):
        self.issue = {
            "type": {"name": "Task"},
            "labels": [{"name": "dependencies"}],
            "milestone": {"number": 42, "title": "Maintenance"},
            "user": {"type": "Bot"},
        }

    def test_accepts_issue_from_automation_and_repository_specific_milestones(self):
        for title in ("Maintenance", "Unscheduled", "2.0.0", "v2.0.0"):
            with self.subTest(title=title):
                self.issue["milestone"]["title"] = title
                self.assertEqual([], metadata.check_metadata(self.issue))

    def test_flags_each_missing_field(self):
        for field, value, message in (
            ("type", None, "issue type"),
            ("labels", [], "category or topic label"),
            ("milestone", None, "milestone"),
        ):
            with self.subTest(field=field, value=value):
                errors = metadata.check_metadata(dict(self.issue, **{field: value}))
                self.assertEqual(1, len(errors))
                self.assertIn(message, errors[0])

    def test_status_and_planning_labels_need_a_topic(self):
        self.issue["labels"] = [
            {"name": name} for name in
            ("blocked-upstream", "Needs-Triage", "help wanted", "roadmap: next", "severity: high")
        ]
        self.assertEqual(1, len(metadata.check_metadata(self.issue)))
        self.issue["labels"].append({"name": "LibreNMS 26.11"})
        self.assertEqual([], metadata.check_metadata(self.issue))

    def test_body_cannot_substitute_for_metadata(self):
        self.assertEqual(3, len(metadata.check_metadata({"body": "Task, ci, v2.0.0"})))

    def test_rejects_pull_request(self):
        with self.assertRaisesRegex(ValueError, "Expected an issue number"):
            metadata.check_metadata(dict(self.issue, pull_request={}))
