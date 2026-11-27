import importlib.util
from pathlib import Path
import unittest
import io
import json
import tempfile
from unittest.mock import patch

PATH = Path(__file__).resolve().parents[1] / "repository_policy.py"
spec = importlib.util.spec_from_file_location("repository_policy", PATH)
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class PullRequestTitleTests(unittest.TestCase):
    def test_issue_must_exist_before_the_pr_and_not_be_another_pr(self):
        self.assertEqual([("DashyNMS/core", 12)], policy.issue_references("Closes #12", "DashyNMS/core"))
        self.assertEqual([("DashyNMS/mobile", 9)], policy.issue_references("Refs https://github.com/DashyNMS/mobile/issues/9", "DashyNMS/core"))
        self.assertEqual([], policy.issue_references("https://example.net/12", "DashyNMS/core"))
        issue = {"created_at": "2026-01-01T00:00:00Z"}
        self.assertTrue(policy.issue_precedes_pr(issue, "2026-02-01T00:00:00Z"))
        self.assertFalse(policy.issue_precedes_pr(issue, "2025-01-01T00:00:00Z"))
        self.assertTrue(policy.issue_precedes_pr(issue, "2025-01-01T00:00:00Z", "2026-02-01T00:00:00Z"))
        self.assertFalse(policy.issue_precedes_pr(dict(issue, pull_request={}), "2026-02-01T00:00:00Z"))

    def test_dependabot_opened_payload_uses_only_a_valid_earlier_maintenance_issue(self):
        pr = {"body": "Bumps a package", "created_at": "2026-02-01T00:00:00Z",
              "user": {"login": "dependabot[bot]", "type": "Bot"}}
        issue = {"created_at": "2026-01-01T00:00:00Z"}
        def response(*args, **kwargs):
            return io.StringIO(json.dumps(issue))
        with patch.object(policy.urllib.request, "urlopen", side_effect=response) as fetch:
            self.assertEqual([], policy.check_issue(pr, "DashyNMS/core", "token", maintenance_issue="126"))
            self.assertIn("/issues/126", fetch.call_args.args[0].full_url)
            issue["created_at"] = "2026-03-01T00:00:00Z"
            self.assertTrue(policy.check_issue(pr, "DashyNMS/core", "token", maintenance_issue="126"))
            for value in (None, "0", "invalid"):
                self.assertTrue(policy.check_issue(pr, "DashyNMS/core", "token", maintenance_issue=value))
            pr["user"] = {"login": "example-user", "type": "User"}
            self.assertTrue(policy.check_issue(pr, "DashyNMS/core", "token", maintenance_issue="126"))

    def test_optional_multiple_and_breaking_scopes(self):
        for title in ["docs: explain contribution rules", "fix(logs,devices): preserve the filter", "feat(api)!: require explicit credentials"]:
            self.assertEqual([], policy.check_title(title))

    def test_rejects_nonconventional_or_untrusted_bot_exceptions(self):
        for title in ["Bump packages", "fix: Upper case", "fix(UI): lower case", "fix: a result.", "fix: a result #12", "docs: " + "a" * 90]:
            self.assertTrue(policy.check_title(title), title)

    def test_generated_single_and_grouped_titles(self):
        bot = {"login": "dependabot[bot]", "type": "Bot", "id": 49699333}
        # Original titles from desktop#650 and desktop#644, and sensor-definitions#43.
        single = "Bump System.Security.Cryptography.ProtectedData from 10.0.14 to 11.0.0"
        group = "Bump the packages group with 14 updates"
        group_across_directories = "bump the packages group across 1 directory with 5 updates"
        self.assertGreater(len("build(deps): " + single), 72)
        for title in (
            "build: " + single,
            "build: " + group,
            "build(deps): " + single,
            "build(deps): " + group,
            "build(deps): " + group_across_directories,
            "build(deps): [Security] " + single,
        ):
            with self.subTest(title=title):
                self.assertEqual([], policy.check_title(title, bot))
                for author in (None, dict(bot, id=1), dict(bot, type="User"), dict(bot, login="another[bot]")):
                    if title == "build(deps): " + group_across_directories:
                        self.assertEqual([], policy.check_title(title, author))
                    else:
                        self.assertTrue(policy.check_title(title, author))

        for title in ("build(deps): Bump", "build(deps): Bump packages.",
                      "build(deps): Bump packages #12", "fix: Bump packages"):
            self.assertTrue(policy.check_title(title, bot))


class WorkflowPolicyTests(unittest.TestCase):
    def workflow(self, checkout=False):
        return {"permissions": {"contents": "read"}, "jobs": {"build": {"steps": [
            {"uses": "actions/checkout@" + "a" * 40, "with": {"persist-credentials": checkout}}]}}}

    def test_writes_belong_to_jobs_and_pinned_checkout_is_read_only(self):
        data = self.workflow()
        data["jobs"]["build"]["permissions"] = {"contents": "write"}
        self.assertEqual([], policy.workflow_errors(data, "ci.yml"))
        data["permissions"] = "write-all"
        self.assertTrue(policy.workflow_errors(data, "ci.yml"))

    def test_python_and_secret_scans_survive_an_earlier_failure(self):
        data = self.workflow()
        scan = {"run": "python scripts/security_check.py"}
        data["jobs"]["build"]["steps"].append(scan)
        self.assertTrue(policy.workflow_errors(data, "security.yml"))
        scan["if"] = "${{ !cancelled() }}"
        self.assertEqual([], policy.workflow_errors(data, "security.yml"))

    def test_privileged_metadata_workflows_cannot_check_out_code(self):
        data = self.workflow()
        for trigger in ("pull_request_target", ["pull_request_target"], {"pull_request_target": {"types": ["opened"]}}):
            data["on"] = trigger
            self.assertTrue(policy.workflow_errors(data, "metadata.yml"))
        data["on"] = ["push", "pull_request"]
        self.assertEqual([], policy.workflow_errors(data, "ci.yml"))

    def test_both_workflow_extensions_and_reusable_permission_contracts(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            workflows = root / ".github/workflows"
            workflows.mkdir(parents=True)
            (root / ".github/repository-config.json").write_text("{}")
            (root / ".github/dependabot.yml").write_text("updates: []")
            with patch.object(policy, "check_links", return_value=[]):
                for extension in ("yml", "yaml"):
                    path = workflows / ("ci." + extension)
                    data = self.workflow()
                    data["jobs"]["build"]["steps"].append({"uses": "example/action@v1"})
                    path.write_text(policy.yaml.safe_dump(data))
                    self.assertTrue(any("full commit SHAs" in e for e in policy.check_repository(root, True)))
                    path.unlink()
                (workflows / "security.yml").write_text("permissions: {contents: read, issues: read}\njobs: {}\n")
                caller = workflows / "ci.yml"
                caller.write_text("permissions: {contents: read}\njobs: {security: {uses: './.github/workflows/security.yml'}}\n")
                self.assertTrue(any("requires issues: read" in e for e in policy.check_repository(root, True)))
                caller.write_text("permissions: {contents: read, issues: read}\njobs: {security: {uses: './.github/workflows/security.yml'}}\n")
                self.assertFalse(any("reusable workflow requires" in e for e in policy.check_repository(root, True)))
                (workflows / "security.yml").write_text(
                    "permissions: {contents: read}\n"
                    "jobs:\n  label:\n    permissions: {contents: read, issues: write}\n    steps: []\n"
                )
                self.assertTrue(any("requires issues: write" in e for e in policy.check_repository(root, True)))
                caller.write_text(
                    "permissions: {contents: read}\n"
                    "jobs:\n  security:\n    permissions: {contents: read, issues: write}\n"
                    "    uses: './.github/workflows/security.yml'\n"
                )
                self.assertFalse(any("reusable workflow requires" in e for e in policy.check_repository(root, True)))

    def test_privileged_callers_cannot_check_out_via_local_reusable_workflows(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            workflows = root / ".github/workflows"
            workflows.mkdir(parents=True)
            (root / ".github/repository-config.json").write_text("{}")
            (root / ".github/dependabot.yml").write_text("updates: []")
            (workflows / "leaf.yml").write_text(
                "on: workflow_call\npermissions: {contents: read}\njobs:\n  build:\n    steps:\n"
                f"      - uses: actions/checkout@{'a' * 40}\n        with: {{persist-credentials: false}}\n"
            )
            (workflows / "middle.yml").write_text(
                "on: workflow_call\npermissions: {contents: read}\n"
                "jobs: {leaf: {uses: './.github/workflows/leaf.yml'}}\n"
            )
            caller = workflows / "caller.yml"
            with patch.object(policy, "check_links", return_value=[]):
                caller.write_text(
                    "on: pull_request\npermissions: {contents: read}\n"
                    "jobs: {middle: {uses: './.github/workflows/middle.yml'}}\n"
                )
                self.assertFalse(any("pull_request_target" in e for e in policy.check_repository(root, True)))
                caller.write_text(
                    "on: pull_request_target\npermissions: {contents: read}\n"
                    "jobs: {middle: {uses: './.github/workflows/middle.yml'}}\n"
                )
                self.assertTrue(any("leaf.yml/build: metadata-only pull_request_target" in e for e in policy.check_repository(root, True)))

    def test_duplicate_checkout_keys_fail_before_workflow_policy_runs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            workflows = root / ".github/workflows"
            workflows.mkdir(parents=True)
            (root / ".github/repository-config.json").write_text("{}")
            (root / ".github/dependabot.yml").write_text("updates: []")
            (workflows / "ci.yml").write_text(
                "jobs: {security: {uses: './.github/workflows/security.yaml'}}\n"
            )
            with patch.object(policy, "check_links", return_value=[]):
                for duplicate in ("with", "WITH"):
                    with self.subTest(duplicate=duplicate):
                        (workflows / "security.yaml").write_text(
                            "on: workflow_call\njobs:\n  audit:\n    steps:\n"
                            f"      - uses: actions/checkout@{'a' * 40}\n"
                            "        with: {persist-credentials: true}\n"
                            f"        {duplicate}: {{persist-credentials: false}}\n"
                        )
                        errors = policy.check_repository(root, True)
                        self.assertEqual(1, sum("duplicate workflow key" in error for error in errors))
                        self.assertTrue(any(".github/workflows/security.yaml:7:9:" in error for error in errors))
                        self.assertFalse(any("reusable workflow not found" in error for error in errors))

    def test_credentials_require_an_explicit_per_job_exception(self):
        data = self.workflow(True)
        self.assertTrue(policy.workflow_errors(data, "publish.yml"))
        self.assertEqual([], policy.workflow_errors(data, "publish.yml", ["publish.yml:build"]))

    def test_blocks_mutable_action_refs_and_event_script_injection(self):
        data = self.workflow()
        data["jobs"]["build"]["steps"] += [{"uses": "example/action@v1"}, {"run": "echo '${{ github.event.pull_request.title }}'"}]
        self.assertEqual(2, len(policy.workflow_errors(data, "ci.yml")))


if __name__ == "__main__":
    unittest.main()
