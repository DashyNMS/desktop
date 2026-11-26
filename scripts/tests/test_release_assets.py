import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('release_assets',
    Path(__file__).resolve().parents[1] / 'upload-missing-release-assets.py')
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


class ReleaseAssetsTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.folder = Path(folder.name)
        (self.folder / 'archive.zip').write_bytes(b'original archive')
        (self.folder / 'SHA256SUMS').write_bytes(b'original manifest')

    def asset(self, name, data):
        return {'name': name, 'digest': 'sha256:' + hashlib.sha256(data).hexdigest()}

    def test_partial_upload_keeps_matching_files_and_uploads_only_missing(self):
        missing = assets.plan_uploads(self.folder,
            [self.asset('archive.zip', b'original archive')], lambda _: self.fail('unexpected download'))
        self.assertEqual(missing, [self.folder / 'SHA256SUMS'])

    def test_rebuilt_archive_cannot_be_mixed_with_an_existing_manifest(self):
        with self.assertRaisesRegex(ValueError, 'different bytes'):
            assets.plan_uploads(self.folder,
                [self.asset('archive.zip', b'previous signed archive')], lambda _: b'')

    def test_different_manifest_is_rejected_even_when_archives_match(self):
        with self.assertRaisesRegex(ValueError, 'SHA256SUMS'):
            assets.plan_uploads(self.folder,
                [self.asset('SHA256SUMS', b'previous manifest')], lambda _: b'')

    def test_old_assets_without_digest_are_downloaded_and_verified(self):
        missing = assets.plan_uploads(self.folder,
            [{'name': 'archive.zip', 'id': 1}], lambda _: b'original archive')
        self.assertEqual(missing, [self.folder / 'SHA256SUMS'])

    def test_files_can_be_named_one_by_one(self):
        missing = assets.plan_uploads([self.folder / 'archive.zip'], [], lambda _: b'')
        self.assertEqual(missing, [self.folder / 'archive.zip'])
        self.assertEqual(2, len(assets.plan_uploads([self.folder], [], lambda _: b'')))

    def test_a_complete_matching_release_needs_no_upload(self):
        existing = [self.asset(p.name, p.read_bytes()) for p in self.folder.iterdir()]
        self.assertEqual(assets.plan_uploads(self.folder, existing, lambda _: b''), [])
