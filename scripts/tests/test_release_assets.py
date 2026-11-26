import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
import os
from unittest.mock import patch

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
        self.manifest = (hashlib.sha256(b'original archive').hexdigest() + '  archive.zip\n').encode()
        (self.folder / 'SHA256SUMS').write_bytes(self.manifest)

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
        self.assertEqual(assets.plan_uploads(self.folder, existing, lambda a: (self.folder / a['name']).read_bytes()), [])

    def test_archive_only_retry_must_match_published_manifest(self):
        published = [self.asset('SHA256SUMS', self.manifest)]
        path = self.folder / 'archive.zip'
        self.assertEqual([path], assets.plan_uploads([path], published, lambda _: self.manifest))
        path.write_bytes(b'rebuilt archive')
        with self.assertRaisesRegex(ValueError, 'SHA256SUMS'):
            assets.plan_uploads([path], published, lambda _: self.manifest)

    def test_leading_period_is_rejected_even_when_manifest_matches(self):
        archive = self.folder / '.archive.zip'
        archive.write_bytes(b'original archive')
        manifest = self.folder / 'SHA256SUMS'
        manifest.write_bytes(self.manifest.replace(b'archive.zip', b'.archive.zip'))
        with self.assertRaisesRegex(ValueError, 'GitHub would rename it'):
            assets.plan_uploads([archive, manifest], [], lambda _: self.fail('unexpected download'))

    def test_new_manifest_must_match_already_published_archive(self):
        with self.assertRaisesRegex(ValueError, 'published archive.zip'):
            assets.plan_uploads([self.folder / 'SHA256SUMS'],
                [self.asset('archive.zip', b'different archive')], lambda _: b'')

    def test_starter_placeholder_has_actionable_cleanup_and_no_download(self):
        with self.assertRaisesRegex(ValueError, 'delete only this failed-upload placeholder'):
            assets.plan_uploads(self.folder, [{'name': 'archive.zip', 'state': 'starter'}],
                                lambda _: self.fail('must not download starter'))

    def test_repository_is_resolved_once_when_gh_repo_is_unset(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(assets.subprocess, 'check_output',
                side_effect=['example/project\n', '12\n', b'{"assets": []}']) as command, \
                patch.object(assets.subprocess, 'run') as upload, \
                patch('sys.argv', ['upload', 'v2.0.0', str(self.folder / 'archive.zip')]):
            assets.main()
        self.assertEqual('repo', command.call_args_list[0].args[0][1])
        self.assertIn('example/project', command.call_args_list[1].args[0])
        self.assertIn('repos/example/project/releases/12', command.call_args_list[2].args[0])
        self.assertIn('example/project', upload.call_args.args[0])
