import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

PATH = Path(__file__).resolve().parents[1] / "security_check.py"
spec = importlib.util.spec_from_file_location("security_check", PATH)
security = importlib.util.module_from_spec(spec)
spec.loader.exec_module(security)


class AuditFailureTests(unittest.TestCase):
    def test_nuget_audit_promotes_required_codes_without_overwriting_project_warnings(self):
        command = security.nuget_restore("sample.csproj")
        self.assertIn("-warnaserror:NU1900;NU1903;NU1904;NU1905", command)
        self.assertIn("-p:NuGetAuditLevel=high", command)
        self.assertFalse(any(arg.startswith("-p:WarningsAsErrors") for arg in command))

    def test_a_failed_restore_does_not_hide_later_results_or_evidence(self):
        commands = [["dotnet", "restore", "first.csproj"], ["dotnet", "restore", "second.csproj"]]
        results = [subprocess.CompletedProcess(commands[0], 1), subprocess.CompletedProcess(commands[1], 0)]
        with tempfile.TemporaryDirectory() as folder, patch.object(security.subprocess, "run", side_effect=results) as run:
            output = Path(folder)
            self.assertTrue(security.run_commands(commands, output))
            self.assertEqual(2, run.call_count)
            evidence = json.loads((output / "nuget-audit.json").read_text())
            self.assertEqual([{"project": "first.csproj", "returncode": 1},
                              {"project": "second.csproj", "returncode": 0}], evidence["projects"])


class SecretBaselineTests(unittest.TestCase):
    def report(self, path="tests/fixture.cs", digest="reviewed", line=3):
        return {"results": {path: [{"type": "Secret Keyword", "hashed_secret": digest, "line_number": line, "reviewed_reason": "Synthetic fixture"}]}}

    def test_reviewed_fixture_can_move_but_new_values_and_files_fail(self):
        baseline = self.report()
        self.assertEqual([], security.new_secrets(self.report(line=45), baseline))
        self.assertTrue(security.new_secrets(self.report(digest="new"), baseline))
        self.assertTrue(security.new_secrets(self.report(path="src/config.cs"), baseline))

    def test_baseline_paths_are_portable_without_ignoring_new_values(self):
        windows = r"tests\fixture.cs"
        self.assertEqual([], security.new_secrets(self.report(path=windows), self.report()))
        self.assertEqual([], security.new_secrets(self.report(), self.report(path=windows)))
        self.assertTrue(security.new_secrets(self.report(path=windows, digest="new"), self.report()))
        self.assertTrue(security.new_secrets(self.report(path="Tests/fixture.cs"), self.report()))

    def test_reports_location_and_type_without_secret_or_digest(self):
        self.assertEqual([("tests/fixture.cs", 3, "Secret Keyword")], security.new_secrets(self.report(), {"results": {}}))


if __name__ == "__main__":
    unittest.main()
