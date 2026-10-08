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


def validate_tag(tag, version):
    if not re.fullmatch(r'v\d+\.\d+\.\d+(?:\.\d+)?', tag):
        raise ValueError('Release tag must be vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH.REVISION')
    parts = tag[1:].split('.')
    normalized = '.'.join(parts + ['0'] * (4 - len(parts)))
    if normalized != version:
        raise ValueError(f'Release tag {tag} does not match manifest version {version}')


def validate_repository(root, tag=None):
    manifest = json.loads((root / 'manifest.json').read_text())
    if len(manifest) != 1 or not manifest[0]['versions']:
        raise ValueError('Manifest must describe one plugin and at least one version')
    plugin = manifest[0]
    release = plugin['versions'][0]
    uuid.UUID(plugin['guid'])
    for value in (release['version'], release['targetAbi']):
        if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', value):
            raise ValueError(f'Expected a four-part plugin/ABI version, got {value}')
    if tag:
        validate_tag(tag, release['version'])
    project = ET.parse(root / 'jellyfin-anidoki/jellyfin-anidoki.csproj')
    for key in ('Version', 'AssemblyVersion', 'FileVersion'):
        if project.findtext(f'.//{key}') != release['version']:
            raise ValueError(f'Project {key} must match manifest version')
    abi_parts = release['targetAbi'].split('.')
    package_version = '.'.join(abi_parts[:3]) if abi_parts[3] == '0' else release['targetAbi']
    for ref in project.findall('.//PackageReference'):
        if ref.attrib['Include'].startswith('Jellyfin.') and ref.attrib['Version'] != package_version:
            raise ValueError(f'{ref.attrib["Include"]} must match target ABI {release["targetAbi"]}')
    build = (root / 'jellyfin-anidoki/build.yaml').read_text()
    expected = {key: plugin[key] for key in ('name', 'guid', 'owner', 'overview', 'description', 'category')}
    expected.update({key: release[key] for key in ('version', 'targetAbi')})
    expected['framework'] = project.findtext('.//TargetFramework')
    for key, value in expected.items():
        match = re.search(rf'^{key}: (.+)$', build, re.MULTILINE)
        if not match or json.loads(match[1]) != value:
            raise ValueError(f'build.yaml {key} must match manifest/project metadata')
    if release['changelog'] not in build:
        raise ValueError('build.yaml changelog must match the current manifest version')
    plugin_source = (root / 'jellyfin-anidoki/Plugin.cs').read_text()
    frontend = (root / 'jellyfin-anidoki/Configuration/ConfigPageJs.js').read_text()
    if f'Guid.Parse("{plugin["guid"]}")' not in plugin_source or f"pluginUniqueId: '{plugin['guid']}'" not in frontend:
        raise ValueError('Plugin and configuration page GUIDs must match the manifest')
    for field, declaration in (('name', 'Name'), ('description', 'Description')):
        if f'{declaration} => "{plugin[field]}"' not in plugin_source:
            raise ValueError(f'Plugin {field} must match manifest')
    return manifest


def package(manifest, publish_dir, output_dir, tag, repository):
    result = copy.deepcopy(manifest)
    plugin = result[0]
    release = plugin['versions'][0]
    validate_tag(tag, release['version'])
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', repository):
        raise ValueError('Repository must have the form owner/name')
    assembly = publish_dir / DLL
    if not assembly.is_file():
        raise ValueError(f'Published plugin DLL is missing: {assembly}')
    release['timestamp'] = datetime.now(timezone.utc).isoformat(timespec='seconds').replace('+00:00', 'Z')
    metadata = {key: plugin[key] for key in ('category', 'guid', 'name', 'description', 'owner', 'overview')}
    metadata.update({key: release[key] for key in ('version', 'targetAbi', 'timestamp', 'changelog')})
    metadata.update({'status': 'Active', 'autoUpdate': True, 'assemblies': [DLL]})
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / f'anidoki_{release["version"]}.zip'
    with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as bundle:
        bundle.write(assembly, DLL)
        bundle.writestr('meta.json', json.dumps(metadata, indent=2) + '\n')
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
        'The ZIP contains the plugin DLL and meta.json. Verify it with the attached checksum files.\n\n'
        'Update the development installation feed by copying the attached manifest.json into a pull request.\n'
    )
    return assets


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('check', 'package'))
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--tag')
    parser.add_argument('--repository')
    parser.add_argument('--publish-dir', type=Path)
    parser.add_argument('--output', type=Path, default=Path('dist'))
    args = parser.parse_args()
    try:
        manifest = validate_repository(args.root, args.tag)
        if args.command == 'package':
            if not all((args.tag, args.repository, args.publish_dir)):
                parser.error('package requires --tag, --repository, and --publish-dir')
            assets = package(manifest, args.publish_dir, args.output, args.tag, args.repository)
            print(f'Created {assets["zip"]}')
        else:
            print('Manifest, build metadata, plugin identity, and project versions match.')
    except (ValueError, KeyError, OSError, ET.ParseError) as error:
        parser.exit(1, f'Release validation failed: {error}\n')


if __name__ == '__main__':
    main()
