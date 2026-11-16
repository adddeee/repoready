# RepoReady

RepoReady is a .NET 10 command-line tool that checks public GitHub repositories for practical onboarding and collaboration signals. It reports evidence and recommendations for each check; it does not judge source-code correctness or support private repositories.

## Checks

The scanner reports `Passed`, `Failed`, or `Unknown` for:

- Root README presence, an introduction signal, and recognizable setup/run instructions (three separate checks)
- A contribution guide in the root, `docs/`, or `.github/`
- A common root license file in the resolved commit's tree
- GitHub Actions workflow presence and recognizable test commands (separate checks)
- A concrete Issue template under `.github/ISSUE_TEMPLATE/`; `config.yml` by itself does not count

Text checks are deliberately heuristic. The report states the exact evidence and the JSON schema uses stable camel-case fields. A truncated Git tree never causes an absent-file `Failed` result: affected absence checks become `Unknown`.

License evidence is bound to the resolved commit: candidate files are discovered in that commit's tree and read by blob SHA. Repository-level `license.spdx_id` is deliberately ignored because it describes current repository metadata, not necessarily the requested historical commit. If the target tree is complete and has no candidate license file, the check is `Failed` even when the default branch has a license. For a truncated tree it is `Unknown` unless a candidate file was actually found. A found file is evidence of presence only, not a verification of license text or legal applicability.

### README introduction signal

`readme-introduction` is a structural heuristic, not semantic understanding. It uses the same selected root README as the usage check: shortest path first, then ordinal path order. It looks for a single plain-text line containing at least **20 Unicode letters or digits** in the opening section (an optional first Markdown H1 is treated as the project title), or immediately within a recognized ATX heading section. Supported heading names are `About`, `Overview`, `Introduction`, `Project description` (case-insensitive), `简介`, `项目简介`, `项目介绍`, and `概述`. Each subsequent heading starts a new section; other section names do not count.

Blank lines, headings alone, fenced or indented code, HTML comment lines, naked URLs, and lines beginning with markup (including badges/images, links, HTML tags, lists, blockquotes, and tables) do not count. This is not a full Markdown parser: Setext/RST headings, descriptions split across short lines, and semantically valid descriptions outside the recognized regions may be missed. Conversely, sufficiently long prose in those regions may pass without explaining the project well. A pass reports the README path, 1-based line number, letter/digit count, section, and up to 160 Unicode characters of the matched line; it does not claim that the text's meaning was understood. If no signal is found, a complete tree yields `Failed` and a truncated tree yields `Unknown`.

Reports now contain eight checks, including the new stable ID `readme-introduction`; the existing JSON report and check fields are unchanged. CLI and Action both use the same rule set.

## Build and run from source

The solution is `RepoReady.slnx` (the .NET 10 SDK's default solution format).

```sh
dotnet build RepoReady.slnx
dotnet run --project tests/RepoReady.Core.Tests
dotnet run --project src/RepoReady.Cli -- check --repo octocat/Hello-World --format json
```

After `RepoReady.Tool` is released to NuGet, install the global command with:

```sh
dotnet tool install --global RepoReady.Tool --version 0.1.0
```

Update it later with `dotnet tool update --global RepoReady.Tool --version <version>`.

The command accepts `owner/repo` or an HTTPS `github.com` URL. `--ref` selects a branch, tag, or commit; by default the repository's default branch is scanned. Results always include the resolved commit SHA.

```text
repoready check --repo owner/repo [--ref <ref>] [--format text|json] [--fail-on none|failed|unknown]
```

Exit codes: `0` means the scan completed below the selected threshold; `1` means the selected readiness threshold was reached; `2` means invalid input or an incomplete scan such as a GitHub API or network error. The default `--fail-on failed` ignores `Unknown` when deciding the exit code. Use `--fail-on unknown` to make indeterminate checks fail a gate, or `none` for informational scans.

The public GitHub REST API is queried without a token. HTTP/API errors are reported as scan failures (exit code 2), never converted to missing-file results. The client uses repository metadata, the resolved commit, its recursive tree, and only candidate file blobs. If GitHub truncates the tree, absence is not asserted.

## GitHub Action

The composite action is in `.github/actions/repoready`. It installs a specified published `RepoReady.Tool` version, emits the same JSON report as the CLI, writes a step summary, and uses the CLI's exit threshold. The package is not published yet, so the action becomes runnable by consumers after `RepoReady.Tool` is released to NuGet. Publishing packages, releases, and a public Action tag is intentionally not performed as part of this source implementation.

Example caller workflow (after publishing and tagging this repository):

```yaml
name: RepoReady
on:
  pull_request:
  push:
    branches: [main]
permissions:
  contents: read
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: owner/repoready/.github/actions/repoready@v0.1.0
        with:
          repository: ${{ github.event.pull_request.head.repo.full_name || github.repository }}
          # Scan the contributor's head commit on PRs; github.sha is the merge commit there.
          ref: ${{ github.event.pull_request.head.sha || github.sha }}
          fail-on: failed
```

For pull requests this example scans the head commit from the source repository, including forks; for push events it scans `github.sha`. Both are immutable SHAs. The source must be public, consistent with the first-release scope.

## Project layout and verification

- `src/RepoReady.Core`: GitHub API client, immutable snapshot model, readiness rules
- `src/RepoReady.Cli`: command-line arguments, text/JSON output, exit-code policy
- `tests/RepoReady.Core.Tests`: dependency-free focused test runner, including mocked HTTP failure behavior
- `.github/actions/repoready`: composite Action wrapper; it contains no duplicate check rules

Run the test runner with `dotnet run --project tests/RepoReady.Core.Tests`. CI runs restore, Release build, and the same tests. This project has no license file: a maintainer should select the intended license before publication rather than inheriting the scanner's findings as a legal choice.

## Work breakdown and current status

| Task | Result |
|---|---|
| Define check IDs, evidence, status semantics, and SHA-traceable report | Complete |
| Implement public GitHub API snapshot reader and explicit API/truncation errors | Complete |
| Implement eight readiness checks and focused offline tests | Complete |
| Implement CLI text/JSON output, thresholds, help, and exit codes | Complete |
| Prepare shared-logic composite Action and PR/push SHA guidance | Source complete; requires a published NuGet tool package and version tag to run for consumers |
| Publish NuGet package, GitHub Release, and public Action tag | Not performed; requires an owner-selected repository license and authorized publishing credentials |
