# SPDX-License-Identifier: AGPL-3.0-only
"""Run the canonical forbidden-term scan (WP-05.02) for ArcScope and write its source-bound evidence.

The scanner and its data are the two named build-time assets of the exact published ArcForges.Contracts.Validation
package selected by naming-package.json. Its NuGet identity is checked once at acquisition, only those two assets are
extracted into a temporary directory, and no second naming registry or sibling source checkout is used.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PIN = Path(__file__).resolve().parent / 'naming-package.json'
ASSETS = ('tools/naming/eng/check_naming.py', 'tools/naming/eng/policy/product-names.json')
OWNER = 'ArcScope'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def git(root, *args):
    return subprocess.run(['git', '-C', str(root), *args], check=True, capture_output=True).stdout.decode().strip()


def read_pin():
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'duplicate pin key: ' + key)
            result[key] = value
        return result
    pin = json.loads(PIN.read_text(encoding='utf-8'), object_pairs_hook=unique)
    require(set(pin) == {'package', 'version', 'contentHash', 'sourceCommit'}, 'unexpected naming pin fields')
    require(pin['package'] == 'ArcForges.Contracts.Validation', 'wrong canonical naming package')
    require(re.fullmatch(r'\d+\.\d+\.\d+(?:-ci\.\d+\.\d+)?', pin['version']), 'naming package must have an exact version')
    require(re.fullmatch('[0-9a-f]{40}', pin['sourceCommit']), 'invalid naming source commit')
    require(len(base64.b64decode(pin['contentHash'], validate=True)) == 64, 'invalid naming package content hash')
    return pin


def acquire(pin, parent):
    package, version = pin['package'].lower(), pin['version'].lower()
    url = f'https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.{version}.nupkg'
    body = b''
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=60) as response:
                body = response.read(32 * 1024 * 1024 + 1)
            break
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            if attempt == 2:
                raise
            time.sleep(2 ** attempt)
    require(len(body) <= 32 * 1024 * 1024 and hashlib.sha512(body).digest() == base64.b64decode(pin['contentHash']),
            'canonical naming package identity mismatch')
    with zipfile.ZipFile(io.BytesIO(body)) as archive:
        for relative in ASSETS:
            entries = [entry for entry in archive.infolist() if entry.filename == relative]
            require(len(entries) == 1 and not entries[0].is_dir() and entries[0].file_size <= 4 * 1024 * 1024,
                    'missing or ambiguous canonical naming asset')
            target = parent / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(entries[0]))
    return parent / ASSETS[0]


def load(source):
    spec = importlib.util.spec_from_file_location('arcscope_canonical_naming', source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fixtures(scanner, policy):
    """One positive and one negative fixture per canonical forbidden term, in a throwaway Git root."""
    with tempfile.TemporaryDirectory(prefix='arcscope-naming-') as folder:
        target = Path(folder)
        git(target, 'init', '-q')
        git(target, 'remote', 'add', 'origin', f'https://github.com/ArcForges/{OWNER}.git')
        git(target, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
            '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-qm', 'fixture')
        # Reviewed exact provenance exceptions stay part of the canonical policy under test.
        for exception in policy['provenanceExceptions']:
            if exception['repository'] == OWNER:
                output = target / exception['path']
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_bytes((ROOT / exception['path']).read_bytes())
        probe = target / 'src/fixture.txt'
        probe.parent.mkdir(parents=True, exist_ok=True)
        probe.write_text('ArcForges ArcScope', encoding='utf-8')
        require(not scanner.scan_repository(target, OWNER, policy)['findings'], 'canonical positive naming fixture failed')
        count = 0
        for item in policy['forbiddenNames']:
            probe.write_text(item['name'], encoding='utf-8')
            found = scanner.scan_repository(target, OWNER, policy)['findings']
            require(any(f.get('path') == 'src/fixture.txt' and f.get('name') == item['name']
                        and f.get('kind') == 'forbidden content' for f in found),
                    'canonical forbidden term was not rejected')
            count += 1
        require(count > 0, 'no canonical negative naming fixtures')
        return {'positive': 1, 'negative': count}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--assets', type=Path, help='use already extracted assets (local diagnosis only)')
    args = parser.parse_args()
    try:
        pin = read_pin()
        with tempfile.TemporaryDirectory(prefix='arcscope-naming-package-') as folder:
            source = (args.assets / ASSETS[0]) if args.assets else acquire(pin, Path(folder))
            scanner = load(source)
            policy = scanner.load_policy()
            row = scanner.scan_repository(ROOT, OWNER, policy)
            exercised = fixtures(scanner, policy)
        hosted = os.environ.get('GITHUB_ACTIONS') == 'true'
        report = {'schemaVersion': 1, 'evidenceClass': 'source-policy-scan', 'namingPackage': pin,
                  'repository': OWNER, 'commit': row['commit'], 'dirty': row['dirty'], 'status': row['status'],
                  'findings': row['findings'], 'filesScanned': row['filesScanned'], 'fixtures': exercised,
                  'runId': os.environ.get('GITHUB_RUN_ID') if hosted else None,
                  'runAttempt': os.environ.get('GITHUB_RUN_ATTEMPT') if hosted else None}
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(f"{OWNER}: {row['status']}; {row['filesScanned']} files; {len(row['findings'])} findings; "
              f"fixtures {exercised['positive']} positive, {exercised['negative']} negative")
        for finding in row['findings']:
            print(json.dumps(finding))
        return 0 if row['status'] == 'pass' else 1
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        print(f'Naming verification failed: {type(error).__name__}: ' +
              (str(error) if isinstance(error, ValueError) else 'file, Git or network access failed'), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
