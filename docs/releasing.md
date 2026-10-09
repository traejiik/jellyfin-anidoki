# Releasing AniDoki

Pull requests targeting `development` run the required **Build and test** check. It validates manifest/build metadata, runs packaging tests, builds the solution, and runs the .NET unit tests. Pushes to `development` run the same checks. CI uses read-only repository permissions and supports PRs from forks.

## Publish a release

1. Merge your changes into `development` after **Build and test** passes. You do not need to add a manifest version or bump the versions in the project or `build.yaml`.
2. Tag the merged commit with the desired release version and push the tag. For example, `v0.1.1` produces plugin version `0.1.1.0`; four-part tags such as `v0.1.1.1` are also accepted:

   ```sh
   git switch development
   git pull --ff-only origin development
   git tag v0.1.1
   git push origin v0.1.1
   ```

3. The **Release plugin** workflow confirms the tagged commit belongs to development history, validates metadata, and applies the tag version to `Version`, `AssemblyVersion`, `FileVersion`, and `build.yaml` in its disposable build checkout. It builds and tests the plugin, generates release notes through GitHub, and publishes:
   - `anidoki_<version>.zip`: the plugin DLL, Jellyfin `meta.json`, and card artwork.
   - `.zip.md5`: the checksum used by Jellyfin's repository manifest.
   - `.zip.sha256`: an additional checksum for manual verification.
   - `manifest.json`: the completed new release entry, including its version, ABI, changelog, download URL, checksum, and timestamp.
4. The **Update installation feed** job merges that entry into `development/manifest.json` and commits it directly. Published history stays intact; no follow-up PR is needed. The project and `build.yaml` version changes exist only in the release build checkout.

The manifest is published history, so its versions do not control future builds and its list may initially be empty. Keep existing published entries so Jellyfin can still install compatible older releases. The target ABI comes from `jellyfin-anidoki/build.yaml`, and its Jellyfin package references must match. Only change ABI/dependencies when changing the supported Jellyfin version. Local project version fields must remain consistent with the version in `build.yaml`; they do not need a bump for each release.

Normal changes to `development` still require a PR and successful CI. The feed job authenticates with the repository deploy key stored in the `RELEASE_FEED_SSH_KEY` Actions secret. Deploy keys bypass the PR/status-check ruleset; a separate ruleset still blocks branch deletion and force pushes. GitHub applies deploy-key bypass to all repository deploy keys, so additional write keys would receive the same exception. `upstream-master` remains locked against these pushes.

Prerelease tag suffixes are not supported. If publication succeeds but the feed update fails, select **Release plugin → Run workflow**, enter the existing release tag, and run it from `development`. This updates the feed without rebuilding or replacing release assets. An already-current feed is a no-op.

## Automatic changelog

GitHub generates the changelog from merged pull requests since the previous release. The workflow uses the same generated text in the repository manifest, the ZIP's `meta.json`, and the GitHub release notes. You do not maintain a changelog in `build.yaml` or prepare a new manifest entry. Existing published changelogs remain intact.

Write descriptive PR titles: those titles become the changelog entries. Generation or empty-output errors stop publication rather than reusing old notes. Manual feed recovery keeps the changelog from the existing published release.

## Optional Docker image

**Build Docker Container** is manual-only. Select a branch or tag in GitHub Actions and run it to publish Intel/AMD and ARM64 images to `ghcr.io/traejiik/jellyfin-anidoki`. It does not update the Jellyfin installation feed.

## Local checks

```sh
python3 scripts/release.py check
python3 -m unittest discover -s scripts/tests -v
dotnet build jellyfin-anidoki.sln --configuration Release
dotnet test jellyfin-anidoki-unit-tests/jellyfin-anidoki-unit-tests.csproj --configuration Release --no-build
```
