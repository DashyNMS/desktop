"""Recognise declared public integrity hashes without excluding their surrounding files."""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def is_integrity_digest(secret, filename):
    if not re.fullmatch(r"[0-9a-f]{40}(?:[0-9a-f]{24})?", secret):
        return False
    relative = Path(filename)
    if relative == Path('.github/repository-standards.json'):
        data = json.loads((ROOT / relative).read_text())
        values = [data['revision'], data['claude_sha256'], *data['files'].values(), *data.get('helpers', {}).values()]
    elif relative.name == 'provenance.json' and relative.parts[:2] == ('docs', 'dependency-licences'):
        data = json.loads((ROOT / relative).read_text())
        values = [data.get('packageSha256'), data.get('upstreamRevision'), *(v.get('sha256') for v in data.get('files', {}).values())]
    else:
        return False
    return secret in values
