# Contributing to AniDōki

Thanks for helping out. This guide covers how the repository is laid out, how to build and test it, and what a pull request needs.

## Project layout

AniDōki targets .NET 10 and Jellyfin 12.2. The solution is `jellyfin-anidoki.sln`.

- `jellyfin-anidoki/`: the plugin. It contains tracker integrations (`Api/`), provider models, helpers, synchronization and scheduled tasks.
- `jellyfin-anidoki/Configuration/`: settings plus the embedded HTML, JavaScript and CSS for the plugin pages. When you add a resource, register it as an `EmbeddedResource` in `jellyfin-anidoki.csproj`.
- `jellyfin-anidoki-unit-tests/`: NUnit tests for the APIs, helpers and sync flows.
- `scripts/`: release tooling (`release.py`) and its Python and Node tests.
- `.github/workflows/`: CI, releases and the optional Docker image.
- `docs/releasing.md`: how releases are tagged and published.

## Build and test

You need the .NET 10 SDK, Python 3, git and Node 24 (for the UI tests). Run everything from the repository root:

```sh
dotnet restore jellyfin-anidoki.sln
dotnet build jellyfin-anidoki.sln --configuration Release --no-restore
dotnet test jellyfin-anidoki-unit-tests/jellyfin-anidoki-unit-tests.csproj --configuration Release --no-build
python3 scripts/release.py check
python3 -m unittest discover -s scripts/tests -v
node --test scripts/tests/ui/*.test.mjs
```

Rebuild before using `--no-build`. CI runs the same checks on every pull request.

To try a change in Jellyfin, build an installable folder and copy its three files into `plugins/anidoki`, then restart Jellyfin:

```sh
python3 scripts/release.py local
```

The build is versioned from the latest release tag (for example `1.1.1.27`), so logs and bug reports show which commit you are running.

## Coding style

- `.editorconfig` defines the whitespace rules: four-space indents, opening braces on the same line, and LF line endings. Run `dotnet format whitespace jellyfin-anidoki.sln` before pushing. CI fails if it would change anything.
- Prefer file-scoped namespaces in new files.
- Use PascalCase for types and methods, camelCase for locals, and `_camelCase` for private fields.
- Keep one statement per line in new code.
- Nullable reference types are on for the whole plugin. Older files opt out with `#nullable disable` on their first line. When you work in one of them, remove the directive if you can annotate the file without adding warnings.
- Prefer message templates for logging (`_logger.LogInformation("Synced {Title}", title)`) over string interpolation.
- Comments explain why the code works the way it does. History belongs in commit messages.

## Tests

- Use NUnit, Moq and Moq.Contrib.HttpClient. HTTP calls use deterministic fixtures, never live tracker APIs.
- Name suites `*Tests` and give tests descriptive names.
- Changes to synchronization, provider status or episode resolution need a regression test.
- Python tests use `unittest` with `test_*` names.

## Pull requests

- Branch from `development`, name the branch `feat/<topic>`, and target `development`.
- Write commit subjects in the imperative. History uses `feat:`, `fix:`, `test:` and `chore:` prefixes.
- **PR titles become changelog entries**, so make them descriptive for users. Label the PR `enhancement`, `bug` or `documentation` so the changelog groups it, and add `breaking` if users must act on the change (see the versioning policy in [docs/releasing.md](docs/releasing.md)).
- In the description, explain the behaviour change, link any issue, and say how you validated it. Include screenshots for UI changes.
- The **Build and test** check must pass before merging.

## Security

Never commit credentials, tokens or webhook URLs, and keep them out of logs. Report vulnerabilities privately to the maintainer rather than in a public issue.

## Releases

Maintainers release by tagging `development`; the tag is the only place a version is set. The project and `build.yaml` stay at the `0.0.0.0` placeholder, so don't bump them in a PR. See [docs/releasing.md](docs/releasing.md) for how the version is chosen and published.
