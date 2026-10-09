#!/usr/bin/env python3
"""Validate AniDoki metadata and build Jellyfin release assets."""
import argparse
import copy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
from urllib.parse import quote
import uuid
import xml.etree.ElementTree as ET
import zipfile

DLL = 'jellyfin-anidoki.dll'
CARD_IMAGE = 'anidoki-card.png'


def version_from_tag(tag):
    if not re.fullmatch(r'v\d+\.\d+\.\d+(?:\.\d+)?', tag):
        raise ValueError('Release tag must be vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH.REVISION')
    parts = tag[1:].split('.')
    return '.'.join(parts + ['0'] * (4 - len(parts)))


def validate_tag(tag, version):
    if version_from_tag(tag) != version:
        raise ValueError(f'Release tag {tag} does not match manifest version {version}')


def validate_repository(root, tag=None):
    manifest = json.loads((root / 'manifest.json').read_text())
    if len(manifest) != 1 or not isinstance(manifest[0].get('versions'), list):
        raise ValueError('Manifest must describe one plugin with a versions list')
    plugin = manifest[0]
    build = (root / 'jellyfin-anidoki/build.yaml').read_text()
    release = {}
    for key in ('version', 'targetAbi'):
        match = re.search(rf'^{key}: (.+)$', build, re.MULTILINE)
        if not match:
            raise ValueError(f'build.yaml must configure {key}')
        release[key] = json.loads(match[1])
    uuid.UUID(plugin['guid'])
    for value in (release['version'], release['targetAbi']):
        if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', value):
            raise ValueError(f'Expected a four-part plugin/ABI version, got {value}')
    if tag:
        validate_tag(tag, release['version'])
    project = ET.parse(root / 'jellyfin-anidoki/jellyfin-anidoki.csproj')
    for key in ('Version', 'AssemblyVersion', 'FileVersion'):
        if project.findtext(f'.//{key}') != release['version']:
            raise ValueError(f'Project {key} must match build.yaml version')
    abi_parts = release['targetAbi'].split('.')
    package_version = '.'.join(abi_parts[:3]) if abi_parts[3] == '0' else release['targetAbi']
    for ref in project.findall('.//PackageReference'):
        if ref.attrib['Include'].startswith('Jellyfin.') and ref.attrib['Version'] != package_version:
            raise ValueError(f'{ref.attrib["Include"]} must match target ABI {release["targetAbi"]}')
    expected = {key: plugin[key] for key in ('name', 'guid', 'owner', 'overview', 'description', 'category')}
    expected.update({key: release[key] for key in ('version', 'targetAbi')})
    expected['framework'] = project.findtext('.//TargetFramework')
    for key, value in expected.items():
        match = re.search(rf'^{key}: (.+)$', build, re.MULTILINE)
        if not match or json.loads(match[1]) != value:
            raise ValueError(f'build.yaml {key} must match plugin/project metadata')
    plugin_source = (root / 'jellyfin-anidoki/Plugin.cs').read_text()
    frontend = (root / 'jellyfin-anidoki/Configuration/ConfigPageJs.js').read_text()
    if f'Guid.Parse("{plugin["guid"]}")' not in plugin_source or f"pluginUniqueId: '{plugin['guid']}'" not in frontend:
        raise ValueError('Plugin and configuration page GUIDs must match the manifest')
    for field, declaration in (('name', 'Name'), ('description', 'Description')):
        if f'{declaration} => "{plugin[field]}"' not in plugin_source:
            raise ValueError(f'Plugin {field} must match manifest')
    if tag:
        # Publish only this release; merge_feed preserves the live feed's history.
        manifest[0]['versions'] = [release]
    return manifest


def prepare_release(root, tag):
    """Apply the tag version in the disposable build checkout, leaving the feed alone."""
    version = version_from_tag(tag)
    validate_repository(root)
    project_path = root / 'jellyfin-anidoki/jellyfin-anidoki.csproj'
    project = project_path.read_text()
    for field in ('Version', 'AssemblyVersion', 'FileVersion'):
        project = re.sub(rf'(<{field}>)[^<]+(</{field}>)',
                         lambda match: match[1] + version + match[2], project)
    build_path = root / 'jellyfin-anidoki/build.yaml'
    build = re.sub(r'^version: .+$', 'version: ' + json.dumps(version),
                   build_path.read_text(), flags=re.MULTILINE)
    project_path.write_text(project)
    build_path.write_text(build)
    return validate_repository(root, tag)


def package(manifest, publish_dir, output_dir, tag, repository, changelog=None, image_path=None):
    result = copy.deepcopy(manifest)
    plugin = result[0]
    release = plugin['versions'][0]
    validate_tag(tag, release['version'])
    if not isinstance(changelog, str) or not changelog.strip():
        raise ValueError('Packaging requires a non-empty generated changelog')
    release['changelog'] = changelog.strip()
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', repository):
        raise ValueError('Repository must have the form owner/name')
    assembly = publish_dir / DLL
    if not assembly.is_file():
        raise ValueError(f'Published plugin DLL is missing: {assembly}')
    image_path = image_path if image_path is not None else Path(__file__).resolve().parents[1] / 'docs/assets' / CARD_IMAGE
    if not image_path.is_file():
        raise ValueError(f'Plugin card artwork is missing: {image_path}')
    image_bytes = image_path.read_bytes()
    release['timestamp'] = datetime.now(timezone.utc).isoformat(timespec='seconds').replace('+00:00', 'Z')
    metadata = {key: plugin[key] for key in ('category', 'guid', 'name', 'description', 'owner', 'overview')}
    metadata.update({key: release[key] for key in ('version', 'targetAbi', 'timestamp', 'changelog')})
    metadata.update({'status': 'Active', 'autoUpdate': True, 'assemblies': [DLL], 'imagePath': CARD_IMAGE})
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / f'anidoki_{release["version"]}.zip'
    with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as bundle:
        bundle.write(assembly, DLL)
        bundle.writestr('meta.json', json.dumps(metadata, indent=2) + '\n')
        bundle.writestr(CARD_IMAGE, image_bytes)
    data = archive.read_bytes()
    md5 = hashlib.md5(data).hexdigest()  # Jellyfin repository checksum format.
    sha256 = hashlib.sha256(data).hexdigest()
    release['checksum'] = md5
    release['sourceUrl'] = f'https://github.com/{repository}/releases/download/{quote(tag, safe="")}/{archive.name}'
    assets = {'zip': archive, 'md5': Path(str(archive) + '.md5'), 'sha256': Path(str(archive) + '.sha256')}
    assets['md5'].write_text(f'{md5}  {archive.name}\n')
    assets['sha256'].write_text(f'{sha256}  {archive.name}\n')
    (output_dir / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')
    (output_dir / 'release-notes.md').write_text(
        f'{release["changelog"]}\n\nRequires Jellyfin {release["targetAbi"]}.\n\n'
        'The ZIP contains the plugin DLL, meta.json, and card artwork. Copy all three files when installing manually. Verify it with the attached checksum files.\n\n'
        'The release workflow automatically updates the development installation feed.\n'
    )
    return assets


def merge_feed(current, published):
    """Merge released versions without overwriting current development metadata."""
    if len(current) != 1 or len(published) != 1 or current[0]['guid'] != published[0]['guid']:
        raise ValueError('Published manifest plugin identity does not match the development feed')
    result = copy.deepcopy(current)
    versions = result[0]['versions']
    for released in published[0]['versions']:
        # Old template entries in the release attachment may not be published yet.
        if not released.get('checksum') and not released.get('sourceUrl'):
            continue
        if not re.fullmatch(r'[0-9a-f]{32}', released.get('checksum', '')) or not released.get('sourceUrl', '').startswith('https://github.com/'):
            raise ValueError('Feed update requires a fully published release URL and MD5 checksum')
        key = (released['version'], released['targetAbi'])
        versions[:] = [v for v in versions if (v['version'], v['targetAbi']) != key]
        versions.append(copy.deepcopy(released))
    latest = published[0]['versions'][0]
    if not latest.get('checksum') or not latest.get('sourceUrl'):
        raise ValueError('Feed update requires a fully published release')
    versions.sort(key=lambda v: (tuple(map(int, v['version'].split('.'))), tuple(map(int, v['targetAbi'].split('.')))), reverse=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('check', 'prepare', 'package', 'update-feed'))
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--manifest', type=Path)
    parser.add_argument('--released', type=Path)
    parser.add_argument('--tag')
    parser.add_argument('--repository')
    parser.add_argument('--publish-dir', type=Path)
    parser.add_argument('--changelog-file', type=Path)
    parser.add_argument('--output', type=Path, default=Path('dist'))
    args = parser.parse_args()
    try:
        if args.command == 'update-feed':
            if not args.manifest or not args.released:
                parser.error('update-feed requires --manifest and --released')
            current = json.loads(args.manifest.read_text())
            published = json.loads(args.released.read_text())
            result = merge_feed(current, published)
            args.manifest.write_text(json.dumps(result, indent=2) + '\n')
            print('Updated installation feed from the published release.')
            return
        if args.command == 'prepare':
            if not args.tag:
                parser.error('prepare requires --tag')
            prepare_release(args.root, args.tag)
            print(f'Prepared build version {version_from_tag(args.tag)} from {args.tag}.')
            return
        manifest = validate_repository(args.root, args.tag)
        if args.command == 'package':
            if not all((args.tag, args.repository, args.publish_dir, args.changelog_file)):
                parser.error('package requires --tag, --repository, --publish-dir, and --changelog-file')
            assets = package(manifest, args.publish_dir, args.output, args.tag, args.repository, changelog=args.changelog_file.read_text(), image_path=args.root / 'docs/assets' / CARD_IMAGE)
            print(f'Created {assets["zip"]}')
        else:
            print('Plugin identity, build metadata, and project versions match; feed history is independent.')
    except (ValueError, KeyError, OSError, ET.ParseError) as error:
        parser.exit(1, f'Release validation failed: {error}\n')


if __name__ == '__main__':
    main()
