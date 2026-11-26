import importlib.util
from pathlib import Path
import unittest

PATH = Path(__file__).resolve().parents[1] / "security_check.py"
spec = importlib.util.spec_from_file_location("security_check", PATH)
security = importlib.util.module_from_spec(spec)
spec.loader.exec_module(security)


class SecretBaselineTests(unittest.TestCase):
    def report(self, path="tests/fixture.cs", digest="reviewed", line=3):
        return {"results": {path: [{"type": "Secret Keyword", "hashed_secret": digest, "line_number": line, "reviewed_reason": "Synthetic fixture"}]}}

    def test_reviewed_fixture_can_move_but_new_values_and_files_fail(self):
        baseline = self.report()
        self.assertEqual([], security.new_secrets(self.report(line=45), baseline))
        self.assertTrue(security.new_secrets(self.report(digest="new"), baseline))
        self.assertTrue(security.new_secrets(self.report(path="src/config.cs"), baseline))

    def test_reports_location_and_type_without_secret_or_digest(self):
        self.assertEqual([("tests/fixture.cs", 3, "Secret Keyword")], security.new_secrets(self.report(), {"results": {}}))


if __name__ == "__main__":
    unittest.main()
