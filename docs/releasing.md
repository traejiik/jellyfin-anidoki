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
5. The **Update installation feed** job downloads the published manifest and commits its release entries directly to `development`. It preserves newer version entries and changes only `manifest.json`. Jellyfin can then install the release using the existing `development/manifest.json` URL; no follow-up PR is needed.

Normal changes to `development` still require a PR and successful CI. The feed job authenticates with the repository deploy key stored in the `RELEASE_FEED_SSH_KEY` Actions secret. Deploy keys bypass the PR/status-check ruleset; a separate ruleset still blocks branch deletion and force pushes. GitHub applies deploy-key bypass to all repository deploy keys, so additional write keys would receive the same exception. `upstream-master` remains locked against these pushes.

Prerelease tag suffixes are not supported. If publication succeeds but the feed update fails, select **Release plugin → Run workflow**, enter the existing release tag, and run it from `development`. This updates the feed without rebuilding or replacing release assets. An already-current feed is a no-op.

## Optional Docker image

**Build Docker Container** is manual-only. Select a branch or tag in GitHub Actions and run it to publish Intel/AMD and ARM64 images to `ghcr.io/traejiik/jellyfin-anidoki`. It does not update the Jellyfin installation feed.

## Local checks

```sh
python3 scripts/release.py check
python3 -m unittest discover -s scripts/tests -v
dotnet build jellyfin-anidoki.sln --configuration Release
dotnet test jellyfin-anidoki-unit-tests/jellyfin-anidoki-unit-tests.csproj --configuration Release --no-build
```
