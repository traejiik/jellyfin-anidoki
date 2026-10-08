import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location('release', Path(__file__).parents[1] / 'release.py')
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)
ROOT = Path(__file__).parents[2]


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads((ROOT / 'manifest.json').read_text())
        self.manifest[0]['versions'][0].update({
            'version': '0.1.0.0', 'checksum': '', 'sourceUrl': '',
            'timestamp': '2000-01-01T00:00:00Z',
        })

    def test_repository_metadata_matches(self):
        release.validate_repository(ROOT)

    def test_package_contains_plugin_and_matching_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            publish = Path(directory) / 'publish'
            publish.mkdir()
            (publish / 'jellyfin-anidoki.dll').write_bytes(b'test assembly')
            (publish / 'Jellyfin.Model.dll').write_bytes(b'server dependency')
            output = Path(directory) / 'release'
            assets = release.package(self.manifest, publish, output, 'v0.1.0', 'traejiik/jellyfin-anidoki')
            archive = assets['zip']
            with zipfile.ZipFile(archive) as package:
                self.assertEqual(set(package.namelist()), {'jellyfin-anidoki.dll', 'meta.json'})
                metadata = json.loads(package.read('meta.json'))
            entry = json.loads((output / 'manifest.json').read_text())[0]
            version = entry['versions'][0]
            self.assertEqual(metadata['guid'], entry['guid'])
            self.assertEqual(metadata['version'], version['version'])
            self.assertEqual(metadata['targetAbi'], version['targetAbi'])
            self.assertEqual(metadata['assemblies'], ['jellyfin-anidoki.dll'])
            self.assertEqual(version['checksum'], hashlib.md5(archive.read_bytes()).hexdigest())
            self.assertEqual(version['sourceUrl'], f'https://github.com/traejiik/jellyfin-anidoki/releases/download/v0.1.0/{archive.name}')
            self.assertIn(hashlib.sha256(archive.read_bytes()).hexdigest(), assets['sha256'].read_text())
            self.assertIn(version['checksum'], assets['md5'].read_text())
            self.assertNotEqual(version['timestamp'], self.manifest[0]['versions'][0]['timestamp'])
            self.assertEqual(self.manifest[0]['versions'][0]['checksum'], '')

    def test_tag_must_match_manifest_version(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'tag'):
                release.package(self.manifest, Path(directory), Path(directory) / 'out', 'v9.0.0', 'traejiik/jellyfin-anidoki')

    def test_invalid_tag_is_rejected(self):
        for tag in ('main', 'v0.1.0;echo', 'v0.1.0-beta'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.validate_tag(tag, '0.1.0.0')

    def test_four_part_tag_is_supported(self):
        release.validate_tag('v0.1.0.0', '0.1.0.0')

    def test_missing_plugin_dll_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'DLL'):
                release.package(self.manifest, Path(directory), Path(directory) / 'out', 'v0.1.0', 'traejiik/jellyfin-anidoki')


if __name__ == '__main__':
    unittest.main()
