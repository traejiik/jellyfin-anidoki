# Releasing AniDoki

Pull requests targeting `development` run the required **Build and test** check. It validates manifest/build metadata, runs packaging tests, builds the solution, and runs the .NET unit tests. Pushes to `development` run the same checks. CI uses read-only repository permissions and supports PRs from forks.

## Publish a release

1. In a PR, update the first version in `manifest.json`, the version/ABI/changelog in `jellyfin-anidoki/build.yaml`, and `Version`, `AssemblyVersion`, and `FileVersion` in the plugin project. Match the Jellyfin package references to the target ABI. Preserve published versions when adding a new one. The new version's URL and checksum can remain blank until packaging.
2. Merge the PR after **Build and test** passes.
3. Tag the merged commit and push the tag. For version `0.1.0.0`, either `v0.1.0` or `v0.1.0.0` is accepted:

   ```sh
   git switch development
   git pull --ff-only origin development
   git tag v0.1.0
   git push origin v0.1.0
   ```

4. The **Release plugin** workflow confirms the tagged commit belongs to development history, verifies the version, builds and tests it, and publishes a GitHub release containing:
   - `anidoki_<version>.zip`: the plugin DLL and Jellyfin `meta.json`.
   - `.zip.md5`: the checksum used by Jellyfin's repository manifest.
   - `.zip.sha256`: an additional checksum for manual verification.
   - `manifest.json`: the generated feed with download URL, MD5 checksum, and release timestamp filled in.
5. Download the release's `manifest.json`, replace the repository copy on a feature branch, and open a PR into `development`. After merging it, Jellyfin can install the release using the existing `development/manifest.json` URL.

The workflow never pushes directly to the protected development branch. A tag alone publishes the release assets; the feed-update PR makes them available through the Jellyfin catalogue. Prerelease tag suffixes are not supported. To retry a failed workflow, rerun it from Actions; if the release was already published, inspect it before attempting another publication.

## Optional Docker image

**Build Docker Container** is manual-only. Select a branch or tag in GitHub Actions and run it to publish Intel/AMD and ARM64 images to `ghcr.io/traejiik/jellyfin-anidoki`. It does not update the Jellyfin installation feed.

## Local checks

```sh
python3 scripts/release.py check
python3 -m unittest discover -s scripts/tests -v
dotnet build jellyfin-anidoki.sln --configuration Release
dotnet test jellyfin-anidoki-unit-tests/jellyfin-anidoki-unit-tests.csproj --configuration Release --no-build
```
