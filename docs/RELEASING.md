# Tests, builds and releases

`VERSION` is the shared three-part version for SDK packages, tools and browser extensions. Synchronize both extension manifests and their package.json. The upstream OpenAI assembly version remains `2.14.0.0`.

Three separate workflow files run in sequence:

1. **Tests** (`.github/workflows/tests.yml`) runs on every branch push, pull request and manual dispatch. It validates the version in the commit title/body and runs extension and SDK tests on Windows x64, Linux x64, Intel macOS and Apple Silicon macOS. The gate requires at least 798 passing SDK cases, 331 beyond the v1.0.0 baseline. Tests compile their dependencies; release asset builds wait for the complete successful test workflow.
2. **Build** (`.github/workflows/build.yml`) starts only after successful repository tests. It checks out the exact tested SHA, builds SDK packages and extensions, validates Firefox and downloads Chromium through pinned Microsoft.Playwright 1.63.0. Four platform jobs package their matching browser and driver, then check the actual native launcher headlessly in automatic and software rendering modes. Pull request runs do not trigger this workflow chain. Branch pushes produce downloadable Actions artifacts.
3. **Release** (`.github/workflows/release.yml`) starts after a successful Build. It independently checks the linked test run's workflow, repository, conclusion, branch and commit. Only a tested `main` commit publishes. It downloads assets from that specific Build run, requires every asset, generates checksums and creates `vVERSION`.

Every release attaches 13 assets:

- Three NuGet packages: ChatGPTWebSdk, ChatGPTWebSdk.Browser, ChatGPTWebSdk.OpenAI.
- SDK DLL and proxy ZIPs with dependencies and all platform Playwright drivers.
- Chromium and Firefox extension ZIPs and an unsigned Firefox XPI.
- Four complete SDK/browser bundles: Windows x64 ZIP; Linux x64, Intel macOS and Apple Silicon macOS tar.gz.
- SHA256SUMS.txt covering the 12 binary assets.

Complete bundles include a .NET 8 example, SDK DLLs, the optional .NET 10 proxy and Chromium. Tar preserves Unix executable permissions and macOS application symlinks. Browser and driver licenses/notices are included. Linux requires Chromium's system libraries. Bundles contain no HARs, user profiles or credentials.

To release an update:

1. Bump VERSION and the extension manifests/package.json once.
2. Run `./build.ps1 -BuildRoot artifacts/build -BundleBrowser`, verify the requested behavior and inspect archives. Tests run before artifact builds; `-SkipTests` is used by the gated CI Build workflow.
3. Commit with the version in the title or body and AGENTS.md's exact co-author trailer.
4. Push main when the user authorizes it. The three workflows handle testing, builds and publication.

A version tagged at another commit is rejected. An immutable release for the same tested commit and complete asset set is preserved on a rerun. New updates require a new version. v1.0.0 was already immutable when the browser/project follow-up was requested, so that work is released as v1.1.0.

Dispatch Tests manually to restart the chain. Actions use pinned SHAs. Only Release receives repository contents write permission. No NuGet registry or browser store publication is configured. Permanent installation in normal Firefox requires Mozilla signing.
