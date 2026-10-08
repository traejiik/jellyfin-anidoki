# GitHub Workflows Implementation Plan

**Goal:** Validate development PRs and publish installable plugin releases from matching version tags.

**Architecture:** Read-only CI and a separate tag-triggered release job share a standard-library Python metadata/package helper. Manual Docker publication stays independent. Protected feed updates use PRs.

**Tech Stack:** GitHub Actions, .NET 10, Python unittest, GitHub CLI, Docker Buildx.

1. Write release-helper tests covering manifest metadata, ZIP contents, checksum accuracy, tag/version mismatch, and missing DLL failure. Run them before implementation and confirm failure.
2. Implement `scripts/release.py` with `check` and `package` subcommands. Validate manifest, build metadata, frontend/plugin identity, and assembly/package versions. Generate ZIP, `meta.json`, checksums, release notes, and a release manifest.
3. Add `.github/workflows/ci.yml` and `release.yml`; make Docker manual-only and fix its runtime copy path. Use a stable `Build and test` job name.
4. Run Python tests, workflow linting, .NET build/tests, and a package smoke test with the real DLL. Address failures that block the new workflows.
5. Document release steps outside README and update the development ruleset to require the CI check. Verify the live rule without committing or publishing local changes.
