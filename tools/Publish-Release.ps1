param([switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'VERSION') -Raw).Trim()
$taskRelease = Join-Path $taskRoot 'artifacts/release'
$taskNames = @(
    "ChatGPTWebSdk.$taskVersion.nupkg", "ChatGPTWebSdk.Browser.$taskVersion.nupkg", "ChatGPTWebSdk.OpenAI.$taskVersion.nupkg",
    "ChatGPTWebSdk-DLLs-$taskVersion.zip", "ChatGPTWebSdk-Proxy-$taskVersion.zip",
    "chatgpt-web-sdk-auth-chromium-$taskVersion.zip", "chatgpt-web-sdk-auth-firefox-$taskVersion.zip", "chatgpt-web-sdk-auth-firefox-$taskVersion.xpi",
    "ChatGPTWebSdk-Bundle-win-x64-$taskVersion.zip", "ChatGPTWebSdk-Bundle-linux-x64-$taskVersion.tar.gz",
    "ChatGPTWebSdk-Bundle-osx-x64-$taskVersion.tar.gz", "ChatGPTWebSdk-Bundle-osx-arm64-$taskVersion.tar.gz"
)
$taskFiles = @($taskNames | ForEach-Object { Join-Path $taskRelease $_ })
foreach ($taskFile in $taskFiles) { if (!(Test-Path -LiteralPath $taskFile) -or (Get-Item -LiteralPath $taskFile).Length -eq 0) { throw "Required release asset is missing: $([IO.Path]::GetFileName($taskFile))" } }
$taskChecksums = $taskFiles | Sort-Object { [IO.Path]::GetFileName($_) } | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) }
$taskChecksumFile = Join-Path $taskRelease 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($taskChecksumFile, [string[]]$taskChecksums)
if ($ValidateOnly) { Write-Host "Validated $($taskFiles.Count) binary assets and checksums for v$taskVersion."; exit 0 }
if ([string]::IsNullOrWhiteSpace($env:RELEASE_COMMIT) -or $env:RELEASE_COMMIT -notmatch '^[a-f0-9]{40}$') { throw 'RELEASE_COMMIT must identify the exact tested commit.' }
$taskTag = "v$taskVersion"
$taskReleaseJson = gh release view $taskTag --repo $env:GH_REPO --json tagName 2>$null
if ($LASTEXITCODE -eq 0) {
    $taskExistingCommit = gh api "repos/$env:GH_REPO/commits/$taskTag" --jq .sha
    if ($LASTEXITCODE -ne 0 -or $taskExistingCommit -ne $env:RELEASE_COMMIT) { throw 'Version already released from another commit. Bump VERSION.' }
    $taskExisting = gh api "repos/$env:GH_REPO/releases/tags/$taskTag" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the existing release.' }
    if ($taskExisting.immutable) {
        $taskExpected = @($taskNames + 'SHA256SUMS.txt' | Sort-Object)
        if (Compare-Object $taskExpected @($taskExisting.assets.name | Sort-Object)) { throw 'Existing immutable release has an unexpected asset set.' }
        Write-Host "$taskTag is already published from this commit with all expected assets. Its immutable files are preserved."
        exit 0
    }
    gh release upload $taskTag @taskFiles $taskChecksumFile --repo $env:GH_REPO --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Release upload failed.' }
    exit 0
}
$taskNotes = Join-Path $taskRoot "artifacts/release-notes-$taskVersion.md"
[IO.File]::WriteAllText($taskNotes, @"
Source-compatible OpenAI .NET 2.14.0 clients backed by ChatGPT web HTTP endpoints.

Download the Chromium or Firefox authentication extension, then the complete SDK bundle for Windows x64, Linux x64, Intel macOS, or Apple Silicon macOS. Each complete bundle includes the SDK, proxy, dependencies, and pinned Chromium. Extract it completely; Hybrid mode locates browsers/ automatically. Unix bundles use tar.gz to preserve executable permissions and symlinks. Linux needs Chromium's normal system libraries.

Click the authentication extension on a signed-in ChatGPT tab, then call OpenAI.ChatGPTWeb.Initialize(authenticationString). Project chat bindings survive SDK restarts; temporary chats retain context only for the running SDK and never persist their messages or remote IDs to disk.

v1.3.0 adds MCP servers scoped to one chat or one message, while keeping initialization defaults. Every scope accepts independent endpoint URLs, HTTP headers, transport, stdio configuration or an existing client; endpoints may also be shared. SetChatMcp registers a chat's servers, UseMessageMcp grants the next ChatClient/ResponsesClient generation attempt its own servers, and native requests accept WebTurnRequest.Mcp. Initialization, chat and message scopes merge by label, with narrower overrides, optional exclusions and full replacement. Scopes retain the same linked conversation, project/temporary context and SSE streaming through all tool rounds. Registrations are runtime-local and connection secrets remain outside model messages and conversation storage.

MCP supports remote Streamable HTTP, legacy SSE, local stdio and application-supplied clients. The message bridge returns ordinary final-answer items rather than hosted platform MCP output items. Existing allowlists, approval callbacks, limits, per-user proxy permissions and durable uncertain-outcome recovery remain. A confirmed message-only result can be recovered after its scope expires without reconnecting or repeating the tool. Intermediate exchanges remain in the remote ChatGPT thread and may be visible on its website. Proxy request bodies continue to select registered servers and cannot create arbitrary new endpoints.

The release includes 1060 SDK tests (115 new scoped MCP cases) plus 12 extension tests. Real independent MCP processes verify distinct and shared URLs, credentials, transports and owned stdio cleanup. All 55 service operations from the supplied HAR remain mapped. Hybrid browser acceleration defaults to hardware with an owned software-rendering fallback. Headless=true provides invisible server operation; interactive Cloudflare challenges can still require the visible default or a signed-in CDP session. See MCP.md in the SDK/proxy bundles for setup and limits.

The Firefox XPI is unsigned: use a temporary installation in about:debugging. Permanent installation in normal Firefox requires Mozilla signing. BrowserOnly remains reserved.

All SDK and extension tests passed on four platforms before the Build workflow ran. Release publication followed the successful Build run for the exact tested commit. SHA256SUMS.txt covers all 12 binary assets.
"@)
gh release create $taskTag @taskFiles $taskChecksumFile --repo $env:GH_REPO --target $env:RELEASE_COMMIT --title "ChatGPT Web SDK $taskVersion" --notes-file $taskNotes
if ($LASTEXITCODE -ne 0) { throw 'Release creation failed.' }
