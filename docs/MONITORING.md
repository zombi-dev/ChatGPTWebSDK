# Daily ChatGPT compatibility checks

`.github/workflows/monitor.yml` runs every day at **07:15 UTC** and supports manual dispatch on the default branch. GitHub may delay scheduled workflows. It runs all SDK, extension and automation tests before building the example's dependencies.

## Enable authenticated example checks

1. Sign in to ChatGPT and use the SDK authentication extension to copy your export.
2. In repository **Settings → Secrets and variables → Actions**, set the repository secret **CHATGPT_WEB_MONITOR_AUTH** to that string. Use an account you intend the automated example to use.
3. Dispatch **Daily ChatGPT compatibility monitor** and inspect its sanitized `monitor-report` artifact.

The secret is available only to the default-branch probe job. Pull requests and the issue publisher do not receive it. The example creates fresh temporary conversations and sends two short marker prompts for each available supported model family. It therefore consumes normal account usage. Temporary content and remote identifiers are not persisted by the SDK.

The Linux probe runs normal Chromium on a private Xvfb virtual display, so no physical desktop window is needed. This follows [Playwright's Linux CI guidance](https://playwright.dev/dotnet/docs/ci#running-headed). Hardware acceleration uses the existing automatic software fallback. A hosted runner may still encounter an interactive Cloudflare challenge that an unattended job cannot complete. Session cookies/access tokens also expire. These conditions produce **blocked** checks with a `[CHANGE]` incident asking for attention; they do not establish a complete SDK failure. Refresh the secret from the extension when needed. A self-hosted runner with supported browser dependencies may be more suitable for an account that consistently needs interactive sign-in.

## What changes are detected

- The authenticated models response is reduced to a structural hash; values such as account information are discarded. The complete non-private model slug catalog detects additions and removals even when generation uses the restricted default set.
- The temporary Sentinel page can report a hash of public JS/CSS asset URLs and DOM role/test identifiers. It never reports HTML, page text, prompts or authentication. This works when the signed-in page loads but a public HTTP fetch is challenged. An anonymous HTTP page is not compared to a signed-in browser baseline.
- The actual QuickStart app checks official-compatible model discovery, ordinary completion, SSE streaming and continuation in the same linked temporary conversation. It attempts one advertised variant per allowed model family and never resends a possibly accepted failed turn.

An API/UI change or partial/blocked example produces `[CHANGE]`. If every attempted family fails, or the model contract is unusable, the same incident becomes `[CRITICAL]`. A failure mixed with a blocked model remains unconfirmed, so it is `[CHANGE]`. Existing incidents are deduplicated; identical reports do not generate comments. The monitor closes its own issue only after the example and UI checks both pass without a baseline difference. It never closes unrelated issues.

## Maintain the baseline

`tools/monitor/baseline.json` holds only hashes and model identifiers. After reviewing an upstream change and verifying the SDK, refresh it locally:

```powershell
# Build and verify first; keep your export in a private file or environment variable.
./build.ps1 -BuildRoot artifacts/build -BundleBrowser
dotnet run --project examples/QuickStart -p:ChatGPTWebBundleDirectory=artifacts/bundles/linux-x64-1.4.0 -- --smoke-test --visible --auth-file /private/authentication.txt --report artifacts/monitor/smoke.json
node tools/monitor/check.mjs --accept-baseline
```

Use your platform's bundle path and an absolute `ChatGPTWebBundleDirectory` if necessary. Add `--visible` for a desktop browser locally; without it the automated example uses headless mode. Daily CI passes `--visible` inside `xvfb-run` to use its virtual display. `--api-only` tests an external Sentinel/session setup when available. Both generation and UI checks must pass to accept a new baseline. Commit the reviewed baseline with the next versioned update.

The report contains status codes, hashes, model slugs and boolean test results. It contains no response text, marker values, HARs, session IDs or credentials. A monitor infrastructure failure with no example report is reported as an incomplete check.
