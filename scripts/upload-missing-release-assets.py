"""Complete a release upload without replacing assets or mixing different builds.

Usage: upload-missing-release-assets.py <tag> <directory or files...>
Existing files must match the local bytes, including SHA256SUMS. A rebuilt signed
archive may differ; reject that retry instead of breaking the published manifest.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def files_in(paths):
    return sorted(paths[0].iterdir()) if len(paths) == 1 and paths[0].is_dir() else list(paths)


def plan_uploads(folder, assets, download):
    existing = {asset['name']: asset for asset in assets}
    missing = []
    # Validate the whole set before uploading anything.
    for path in files_in([folder] if isinstance(folder, Path) else folder):
        if not path.is_file():
            raise ValueError(f'Expected a release file: {path}')
        asset = existing.get(path.name)
        if asset is None:
            missing.append(path)
            continue
        digest = asset.get('digest')
        if not digest:
            digest = 'sha256:' + hashlib.sha256(download(asset)).hexdigest()
        if digest != 'sha256:' + sha256(path):
            raise ValueError(f'{path.name} is already published with different bytes; '
                             'use the original build artifacts to finish this release')
    return missing


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('tag')
    parser.add_argument('paths', nargs='+', help='a directory, or files')
    args = parser.parse_args()
    tag, paths = args.tag, args.paths
    # By id: a draft can't be read by its tag, and it's how the first edge release starts.
    identity = subprocess.check_output(
        ['gh', 'release', 'view', tag, '--json', 'databaseId', '--jq', '.databaseId'], text=True).strip()
    release = json.loads(subprocess.check_output(
        ['gh', 'api', f"repos/{os.environ['GH_REPO']}/releases/{identity}"]))

    def download(asset):
        return subprocess.check_output(['gh', 'api',
            f"repos/{os.environ['GH_REPO']}/releases/assets/{asset['id']}",
            '-H', 'Accept: application/octet-stream'])

    missing = plan_uploads([Path(path) for path in paths], release['assets'], download)
    if missing:
        subprocess.run(['gh', 'release', 'upload', tag, *map(str, missing)], check=True)


if __name__ == '__main__':
    main()
