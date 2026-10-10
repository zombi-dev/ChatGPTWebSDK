# Tests, builds and releases

`VERSION` is the shared three-part version for SDK packages, tools and browser extensions. Synchronize both extension manifests and their package.json. The upstream OpenAI assembly version remains `2.14.0.0`.

Three separate workflow files run in sequence:

1. **Tests** (`.github/workflows/tests.yml`) runs on every branch push, pull request and manual dispatch. For repository pushes, it validates the Conventional Commit title and version in the commit title/body and runs extension and SDK tests on Windows x64, Linux x64, Intel macOS and Apple Silicon macOS. The gate requires at least 1206 passing SDK cases, plus extension and automation tests. Dependabot pull requests validate and test the version without requiring the generated dependency commit to mention it. Tests compile their dependencies; release asset builds wait for the complete successful test matrix.
2. **Build** (`.github/workflows/build.yml`) is a reusable workflow called only after all repository test jobs succeed. It checks out the exact tested SHA, builds SDK packages and extensions, validates Firefox and downloads Chromium through pinned Microsoft.Playwright 1.63.0. Four platform jobs package their matching browser and driver, then check the actual native launcher headlessly in automatic and software rendering modes. Preparation/download, native verification and archiving run as separate steps with bounded timeouts. Archive creation requires matching SHA256 fingerprints from successful preparation and verification. Pull request runs do not trigger this workflow chain. Branch pushes produce downloadable Actions artifacts.
3. **Release** (`.github/workflows/release.yml`) starts after the successful Tests workflow, whose gated reusable Build has completed. It independently checks the originating test run's workflow, repository, conclusion, event, branch, commit ancestry and exact artifact set. Only a tested `main` commit publishes. It downloads assets from that specific Build run, requires every asset, generates checksums and creates `vVERSION` as a draft, uploads all required assets and publishes last. The write-permission job has no checkout and executes no downloaded code. Versions and release notes are read as bounded data through GitHub APIs.

Native rendering verification also uses `tools/ci/run-bounded.cjs` with a 150-second subprocess deadline. A timed-out command or a descendant retaining output pipes fails the build. SDK diagnostics report driver, browser, rendering and cleanup stages; a failure cannot produce a verified archive receipt.

Every release attaches 13 assets:

- Three NuGet packages: ChatGPTWebSdk, ChatGPTWebSdk.Browser, ChatGPTWebSdk.OpenAI.
- SDK DLL and proxy ZIPs with dependencies and all platform Playwright drivers.
- Chromium and Firefox extension ZIPs and an unsigned Firefox XPI.
- Four complete SDK/browser bundles: Windows x64 ZIP; Linux x64, Intel macOS and Apple Silicon macOS tar.gz.
- SHA256SUMS.txt covering the 12 binary assets.

Complete bundles include a .NET 8 example, SDK DLLs, the optional .NET 10 proxy and Chromium. Tar preserves Unix executable permissions and macOS application symlinks. Browser and driver licenses/notices are included. Linux requires Chromium's system libraries. Bundles contain no HARs, user profiles or credentials.

The Linux Build job installs the pinned Playwright browser dependencies and, when Ubuntu's namespace restriction is present, loads an AppArmor profile scoped to the staged browser path. This grants the sandbox's required user namespace on that disposable runner. It does not add --no-sandbox or change a user's server configuration.

To release an update:

1. Bump VERSION and the extension manifests/package.json once.
2. Run `./build.ps1 -BuildRoot artifacts/build -BundleBrowser`, verify the requested behavior and inspect archives. Tests run before artifact builds; `-SkipTests` is used by the gated CI Build workflow.
3. Split independent changes into focused `type(scope): summary` commits, each ending with AGENTS.md's exact co-author trailer. Finish with `chore(release): X.Y.Z`; that checkpoint triggers the release gate. Write a summary and categorized bullets in `docs/RELEASE_NOTES.md`, with roughly 1.5 times the detail of the earlier brief notes. Release titles are `vX.Y.Z`.
4. Push main when the user authorizes it. The three workflows handle testing, builds and publication.

A version tagged at another commit is rejected. An immutable release for the same tested commit and complete asset set is preserved on a rerun. New updates require a new version. v1.0.0 was already immutable when the browser/project follow-up was requested, so that work is released as v1.1.0.

Dispatch Tests manually to restart the chain. Actions use pinned SHAs. Only Release receives repository contents write permission. The daily monitor separately writes its own issues after consuming a sanitized report; it does not have contents write permission. No NuGet registry or browser store publication is configured. Permanent installation in normal Firefox requires Mozilla signing.

Release and monitor publishers are tested in `tests/automation`. Their implementations are embedded in workflow YAML so privileged jobs never execute files from a triggering checkout. After editing `tools/ci/release.cjs` or `monitor-issue.cjs`, run `node tools/ci/sync-workflows.mjs`; tests reject stale embedded code. Scheduled monitor setup and baseline maintenance are documented in [MONITORING.md](MONITORING.md).

History maintenance keeps published release tags at their original commits. Rebuilt main history is checked against each original release tree and the final tested tree before a guarded force push. A local Git bundle preserves the complete original history. See [CONTRIBUTING.md](../CONTRIBUTING.md) for message conventions.

The workflow reads commit history and published release metadata through GitHub's API, then appends a closed-by-default **All N commits** section with categorized, individually linked subjects. The previous published tag bounds the range; after history reconstruction, its matching source tree identifies the equivalent checkpoint. The appendix explains when it links rebuilt history. The manual publishing script uses the same formatter through `tools/ci/format-release-notes.cjs`.

Unpublished `chore(release): X.Y.Z` checkpoints in that range produce a version note at the very top. For example, v1.4.0 was internal and was not publicly released; its changes shipped in v1.5.0. Version numbers absent from the history are not described as internal releases.
