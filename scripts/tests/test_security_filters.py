import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

path = Path(__file__).resolve().parents[1] / 'security_filters.py'
spec = importlib.util.spec_from_file_location('integrity_filters', path)
filters = importlib.util.module_from_spec(spec)
spec.loader.exec_module(filters)


class IntegrityFilterTests(unittest.TestCase):
    def test_only_declared_metadata_fields_are_exempt(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / '.github').mkdir()
            sha = 'a' * 64
            data = {'revision': 'b' * 40, 'claude_sha256': sha, 'files': {}, 'token': 'c' * 64}
            (root / '.github/repository-standards.json').write_text(json.dumps(data))
            with patch.object(filters, 'ROOT', root):
                self.assertTrue(filters.is_integrity_digest(sha, '.github/repository-standards.json'))
                self.assertFalse(filters.is_integrity_digest('c' * 64, '.github/repository-standards.json'))
                self.assertFalse(filters.is_integrity_digest(sha, 'src/settings.json'))
