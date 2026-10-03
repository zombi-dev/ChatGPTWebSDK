# Builds and releases

`VERSION` is the shared three-part release version for all SDK NuGet packages, tools and browser extensions. Keep both `extensions/chatgpt-auth/manifest.*.json` files and its `package.json` synchronized with that value. The replacement's OpenAI assembly version remains `2.14.0.0` for the pinned upstream C# surface.

Every push to a branch and every pull request runs `.github/workflows/build-release.yml`. The build validates that the commit title or body mentions the current version, runs the SDK and extension tests, validates the Firefox package, builds all artifacts, and uploads a downloadable Actions artifact. A successful push to `main` additionally creates a GitHub release tagged `vVERSION` with:

- All three NuGet packages.
- A ZIP containing the SDK DLLs and their runtime dependencies.
- A ZIP containing the HTTP proxy and its dependencies.
- Chromium and Firefox extension ZIPs and an unsigned Firefox XPI.
- `SHA256SUMS.txt` for the binary assets.

The SDK/proxy bundles include the Playwright driver but no browser. Libraries target .NET 8; the proxy/tools target .NET 10. The workflow installs both runtime families and Node 24. Actions are pinned to their reviewed commit SHAs. The release job alone receives write access to repository contents.

To release an update, complete these steps:

1. Bump `VERSION`, both extension manifests, and the extension `package.json` once for the update.
2. Run `./build.ps1 -BuildRoot artifacts/build` and verify the requested behavior.
3. Commit with the new version in the title or body, for example `v1.0.1: Fix session import`. Follow the exact co-author trailer rule in `AGENTS.md`.
4. Push `main` when the user authorizes pushing. The workflow publishes the release after validation succeeds.

Running the same workflow again for the same commit uploads replacement assets to the same release. A version already tagged at another commit is rejected; bump the version for a new update. Commits without a version fail validation instead of silently producing an unversioned release.

`workflow_dispatch` can retry a committed release manually. No NuGet registry or browser store publication is configured. Firefox permanent installation requires Mozilla signing outside this unsigned development/release packaging flow.
