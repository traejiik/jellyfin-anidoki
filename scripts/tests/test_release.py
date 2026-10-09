import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
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
            image = Path(directory) / 'card.png'
            image_bytes = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
            image.write_bytes(image_bytes)
            self.manifest[0]['imageUrl'] = 'https://example.com/anidoki-card.png'
            output = Path(directory) / 'release'
            assets = release.package(self.manifest, publish, output, 'v0.1.0', 'traejiik/jellyfin-anidoki', changelog='Automatically generated PR notes', image_path=image)
            archive = assets['zip']
            with zipfile.ZipFile(archive) as package:
                self.assertEqual(set(package.namelist()), {'jellyfin-anidoki.dll', 'meta.json', 'anidoki-card.png'})
                self.assertEqual(package.read('anidoki-card.png'), image_bytes)
                metadata = json.loads(package.read('meta.json'))
            entry = json.loads((output / 'manifest.json').read_text())[0]
            self.assertEqual(entry['imageUrl'], self.manifest[0]['imageUrl'])
            self.assertEqual(metadata['imagePath'], 'anidoki-card.png')
            version = entry['versions'][0]
            self.assertEqual(metadata['guid'], entry['guid'])
            self.assertEqual(metadata['version'], version['version'])
            self.assertEqual(metadata['targetAbi'], version['targetAbi'])
            self.assertEqual(metadata['assemblies'], ['jellyfin-anidoki.dll'])
            self.assertEqual(metadata['changelog'], 'Automatically generated PR notes')
            self.assertEqual(version['changelog'], metadata['changelog'])
            self.assertIn(metadata['changelog'], (output / 'release-notes.md').read_text())
            self.assertEqual(version['checksum'], hashlib.md5(archive.read_bytes()).hexdigest())
            self.assertEqual(version['sourceUrl'], f'https://github.com/traejiik/jellyfin-anidoki/releases/download/v0.1.0/{archive.name}')
            self.assertIn(hashlib.sha256(archive.read_bytes()).hexdigest(), assets['sha256'].read_text())
            self.assertIn(version['checksum'], assets['md5'].read_text())
            self.assertNotEqual(version['timestamp'], self.manifest[0]['versions'][0]['timestamp'])
            self.assertEqual(self.manifest[0]['versions'][0]['checksum'], '')

    def test_package_cli_uses_artwork_from_explicit_root(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'repository'
            for name in ('manifest.json', 'jellyfin-anidoki/jellyfin-anidoki.csproj',
                         'jellyfin-anidoki/build.yaml', 'jellyfin-anidoki/Plugin.cs',
                         'jellyfin-anidoki/Configuration/ConfigPageJs.js'):
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / name, target)
            image = root / 'docs/assets/anidoki-card.png'
            image.parent.mkdir(parents=True)
            image_bytes = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
            image.write_bytes(image_bytes)
            publish = Path(directory) / 'publish'
            publish.mkdir()
            (publish / 'jellyfin-anidoki.dll').write_bytes(b'test assembly')
            notes = Path(directory) / 'notes.md'
            notes.write_text('Generated release notes')
            output = Path(directory) / 'release'
            prepared = subprocess.run([
                sys.executable, str(ROOT / 'scripts/release.py'), 'prepare',
                '--root', str(root), '--tag', 'v0.1.1',
            ], capture_output=True, text=True)
            self.assertEqual(prepared.returncode, 0, prepared.stderr)
            result = subprocess.run([
                sys.executable, str(ROOT / 'scripts/release.py'), 'package',
                '--root', str(root), '--publish-dir', str(publish), '--output', str(output),
                '--tag', 'v0.1.1', '--repository', 'traejiik/jellyfin-anidoki',
                '--changelog-file', str(notes),
            ], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            with zipfile.ZipFile(output / 'anidoki_0.1.1.0.zip') as archive:
                self.assertEqual(archive.read('anidoki-card.png'), image_bytes)
                self.assertEqual(json.loads(archive.read('meta.json'))['version'], '0.1.1.0')
            feed = json.loads((output / 'manifest.json').read_text())
            self.assertEqual(len(feed[0]['versions']), 1)
            self.assertEqual(feed[0]['versions'][0]['version'], '0.1.1.0')
            self.assertEqual(len(feed[0]['versions'][0]['checksum']), 32)

    def test_package_rejects_missing_card_artwork_without_creating_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            publish = Path(directory)
            (publish / 'jellyfin-anidoki.dll').write_bytes(b'test assembly')
            output = publish / 'out'
            with self.assertRaisesRegex(ValueError, 'artwork'):
                release.package(self.manifest, publish, output, 'v0.1.0', 'traejiik/jellyfin-anidoki',
                                changelog='Automatically generated PR notes', image_path=publish / 'missing.png')
            self.assertFalse(output.exists())

    def test_package_rejects_missing_or_empty_generated_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            publish = Path(directory)
            (publish / 'jellyfin-anidoki.dll').write_bytes(b'test assembly')
            for notes in (None, '', '  \n'):
                with self.subTest(notes=notes), self.assertRaisesRegex(ValueError, 'changelog'):
                    release.package(self.manifest, publish, publish / 'out', 'v0.1.0', 'traejiik/jellyfin-anidoki', changelog=notes)

    def test_tag_must_match_manifest_version(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'tag'):
                release.package(self.manifest, Path(directory), Path(directory) / 'out', 'v9.0.0', 'traejiik/jellyfin-anidoki', changelog='Automatically generated PR notes')

    def test_invalid_tag_is_rejected(self):
        for tag in ('main', 'v0.1.0;echo', 'v0.1.0-beta'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.validate_tag(tag, '0.1.0.0')

    def test_four_part_tag_is_supported(self):
        release.validate_tag('v0.1.0.0', '0.1.0.0')

    def test_missing_plugin_dll_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'DLL'):
                release.package(self.manifest, Path(directory), Path(directory) / 'out', 'v0.1.0', 'traejiik/jellyfin-anidoki', changelog='Automatically generated PR notes')


class TagDrivenReleaseTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for name in ('manifest.json', 'jellyfin-anidoki/jellyfin-anidoki.csproj',
                     'jellyfin-anidoki/build.yaml', 'jellyfin-anidoki/Plugin.cs',
                     'jellyfin-anidoki/Configuration/ConfigPageJs.js'):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.original = (self.root / 'manifest.json').read_text()

    def test_prepare_uses_tag_and_keeps_published_history_unchanged(self):
        result = release.prepare_release(self.root, 'v0.1.1')
        self.assertEqual(result[0]['versions'], [{'version': '0.1.1.0', 'targetAbi': '12.2.0.0'}])
        self.assertEqual((self.root / 'manifest.json').read_text(), self.original)
        project = release.ET.parse(self.root / 'jellyfin-anidoki/jellyfin-anidoki.csproj')
        for field in ('Version', 'AssemblyVersion', 'FileVersion'):
            self.assertEqual(project.findtext(f'.//{field}'), '0.1.1.0')
        self.assertEqual(release.validate_repository(self.root, 'v0.1.1'), result)
        # Ordinary CI must still pass after the feed publishes the new version.
        (self.root / 'manifest.json').write_text(json.dumps(result))
        release.validate_repository(self.root)

    def test_empty_feed_can_release_using_configured_abi(self):
        manifest = json.loads(self.original)
        manifest[0]['versions'] = []
        (self.root / 'manifest.json').write_text(json.dumps(manifest))
        result = release.prepare_release(self.root, 'v1.2.3.4')
        self.assertEqual(result[0]['versions'][0], {'version': '1.2.3.4', 'targetAbi': '12.2.0.0'})

    def test_feed_version_and_abi_do_not_control_current_build(self):
        manifest = json.loads(self.original)
        manifest[0]['versions'][0].update(version='9.0.0.0', targetAbi='11.0.0.0')
        (self.root / 'manifest.json').write_text(json.dumps(manifest))
        release.validate_repository(self.root)
        self.assertEqual(release.prepare_release(self.root, 'v0.2.0')[0]['versions'][0]['targetAbi'], '12.2.0.0')

    def test_invalid_tag_does_not_modify_build_files(self):
        project = self.root / 'jellyfin-anidoki/jellyfin-anidoki.csproj'
        build = self.root / 'jellyfin-anidoki/build.yaml'
        before = (project.read_text(), build.read_text())
        with self.assertRaises(ValueError):
            release.prepare_release(self.root, 'v1.2.3-beta')
        self.assertEqual((project.read_text(), build.read_text()), before)

    def test_new_release_feed_merge_preserves_old_release_exactly(self):
        candidate = release.prepare_release(self.root, 'v0.1.1')
        candidate[0]['versions'][0].update(checksum='b' * 32, sourceUrl='https://github.com/example/release.zip')
        original = json.loads(self.original)
        result = release.merge_feed(original, candidate)
        self.assertEqual(result[0]['versions'][1:], original[0]['versions'])
        self.assertEqual(result[0]['versions'][0]['version'], '0.1.1.0')


class FeedUpdateTests(unittest.TestCase):
    def setUp(self):
        self.current = json.loads((ROOT / 'manifest.json').read_text())
        self.published = json.loads((ROOT / 'manifest.json').read_text())
        self.published[0]['versions'][0].update({
            'version': '0.1.0.0', 'checksum': 'a' * 32,
            'sourceUrl': 'https://github.com/traejiik/jellyfin-anidoki/releases/download/v0.1.0/anidoki_0.1.0.0.zip',
        })

    def test_fill_in_published_release_without_mutating_input(self):
        self.current[0]['versions'][0].update({'version': '0.1.0.0', 'checksum': '', 'sourceUrl': ''})
        result = release.merge_feed(self.current, self.published)
        self.assertEqual(result[0]['versions'][0]['checksum'], 'a' * 32)
        self.assertEqual(self.current[0]['versions'][0]['checksum'], '')

    def test_older_release_preserves_newer_template_and_metadata(self):
        self.current[0]['versions'][0].update({'version': '0.2.0.0', 'checksum': '', 'sourceUrl': ''})
        self.current[0]['description'] = 'New description on development'
        result = release.merge_feed(self.current, self.published)
        self.assertEqual([v['version'] for v in result[0]['versions']], ['0.2.0.0', '0.1.0.0'])
        self.assertEqual(result[0]['description'], 'New description on development')
        self.assertEqual(result[0]['versions'][0]['checksum'], '')

    def test_same_version_different_abi_is_preserved(self):
        self.current[0]['versions'][0]['version'] = '0.1.0.0'
        self.current[0]['versions'][0]['targetAbi'] = '12.1.0.0'
        result = release.merge_feed(self.current, self.published)
        self.assertEqual(len(result[0]['versions']), 2)

    def test_idempotent_update(self):
        once = release.merge_feed(self.current, self.published)
        self.assertEqual(release.merge_feed(once, self.published), once)

    def test_wrong_plugin_identity_is_rejected(self):
        self.published[0]['guid'] = '00000000-0000-0000-0000-000000000000'
        with self.assertRaisesRegex(ValueError, 'identity'):
            release.merge_feed(self.current, self.published)

    def test_unpublished_release_is_rejected(self):
        self.published[0]['versions'][0]['checksum'] = ''
        with self.assertRaisesRegex(ValueError, 'published'):
            release.merge_feed(self.current, self.published)


if __name__ == '__main__':
    unittest.main()
