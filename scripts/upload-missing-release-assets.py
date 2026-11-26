"""Complete a release upload without replacing assets or mixing different builds.

Usage: upload-missing-release-assets.py <tag> <directory or files...>
Existing files must match the local bytes, including SHA256SUMS. A rebuilt signed
archive may differ; reject that retry instead of breaking the published manifest.
"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def files_in(paths):
    return sorted(paths[0].iterdir()) if len(paths) == 1 and paths[0].is_dir() else list(paths)


def plan_uploads(folder, assets, download):
    existing = {asset['name']: asset for asset in assets}
    selected = files_in([folder] if isinstance(folder, Path) else folder)
    if len({path.name for path in selected}) != len(selected):
        raise ValueError('Release filenames must be unique')
    for asset in assets:
        if asset.get('state') == 'starter':
            raise ValueError(f"{asset['name']} is an incomplete starter asset; delete only this "
                             "failed-upload placeholder in GitHub Releases, then retry with the original artifacts")
    cached = {}

    def data(asset):
        if asset['name'] not in cached:
            cached[asset['name']] = download(asset)
        return cached[asset['name']]

    def published_digest(asset):
        return asset.get('digest') or 'sha256:' + hashlib.sha256(data(asset)).hexdigest()

    def manifest(content):
        entries = {}
        for line in content.decode('utf-8').splitlines():
            match = re.fullmatch(r'([0-9a-fA-F]{64}) [ *](.+)', line)
            if not match or match[2] in entries:
                raise ValueError('SHA256SUMS contains an invalid or duplicate entry')
            entries[match[2]] = match[1].lower()
        if not entries:
            raise ValueError('SHA256SUMS is empty')
        return entries

    missing = []
    # Validate the whole set before uploading anything.
    for path in selected:
        if not path.is_file():
            raise ValueError(f'Expected a release file: {path}')
        if path.name.startswith('.') or path.name.endswith('.'):
            raise ValueError(f'{path.name} starts or ends with a period and GitHub would rename it on upload')
        asset = existing.get(path.name)
        if asset is None:
            missing.append(path)
            continue
        digest = published_digest(asset)
        if digest != 'sha256:' + sha256(path):
            raise ValueError(f'{path.name} is already published with different bytes; '
                             'use the original build artifacts to finish this release')
    # A manifest can be present even when the caller retries only one archive.
    manifests = []
    if 'SHA256SUMS' in existing:
        manifests.append(manifest(data(existing['SHA256SUMS'])))
    manifests.extend(manifest(path.read_bytes()) for path in selected if path.name == 'SHA256SUMS')
    for checksums in manifests:
        for path in selected:
            if path.name == 'SHA256SUMS':
                continue
            if checksums.get(path.name) != sha256(path):
                raise ValueError(f'{path.name} does not match SHA256SUMS; use the original build artifacts')
        for name, digest in checksums.items():
            if name in existing and published_digest(existing[name]) != 'sha256:' + digest:
                raise ValueError(f'SHA256SUMS does not match published {name}')
    return missing


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('tag')
    parser.add_argument('paths', nargs='+', help='a directory, or files')
    args = parser.parse_args()
    tag, paths = args.tag, args.paths
    repository = os.environ.get('GH_REPO') or subprocess.check_output(
        ['gh', 'repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner'], text=True).strip()
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', repository):
        raise ValueError('Expected a GitHub repository in owner/name form')
    # By id: a draft can't be read by its tag, and it's how the first edge release starts.
    identity = subprocess.check_output(
        ['gh', 'release', 'view', tag, '--repo', repository, '--json', 'databaseId', '--jq', '.databaseId'], text=True).strip()
    release = json.loads(subprocess.check_output(
        ['gh', 'api', f"repos/{repository}/releases/{identity}"]))

    def download(asset):
        return subprocess.check_output(['gh', 'api',
            f"repos/{repository}/releases/assets/{asset['id']}",
            '-H', 'Accept: application/octet-stream'])

    missing = plan_uploads([Path(path) for path in paths], release['assets'], download)
    if missing:
        subprocess.run(['gh', 'release', 'upload', tag, '--repo', repository, *map(str, missing)], check=True)


if __name__ == '__main__':
    main()
