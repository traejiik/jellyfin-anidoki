# AniDoki validation and releases

The agreed design separates validation from publication. Pull requests into `development` and pushes to that branch run a stable `Build and test` check using .NET 10. Fork PRs need no secrets or write permissions. The check validates packaging metadata, builds the solution, runs its tests, and tests the release packaging helper.

Version tags build and test the tagged commit before publishing a ZIP, MD5 and SHA-256 checksum files, and a generated repository manifest as GitHub release assets. The tag must match the manifest and project version, and the tagged commit must belong to development history. The ZIP contains the plugin DLL and a Jellyfin `meta.json`; server-provided assemblies are not bundled.

The release does not bypass development protection. Its generated manifest is copied into a normal PR to update the installation feed. Docker publication becomes manual-only, with the container DLL path corrected. The development ruleset requires the stable CI check in addition to its existing PR requirement.

Verification includes packaging tests for invalid versions and missing DLLs, JSON/XML consistency checks, workflow linting, and a real .NET build and test run using a temporary SDK. No release is published during implementation.
