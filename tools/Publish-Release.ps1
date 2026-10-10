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
node (Join-Path $taskRoot 'tools/ci/format-release-notes.cjs') $env:GH_REPO $env:RELEASE_COMMIT $taskVersion (Join-Path $taskRoot 'docs/RELEASE_NOTES.md') $taskNotes
if ($LASTEXITCODE -ne 0) { throw 'Could not format release notes with the complete commit history.' }
gh release create $taskTag @taskFiles $taskChecksumFile --repo $env:GH_REPO --target $env:RELEASE_COMMIT --title $taskTag --notes-file $taskNotes
if ($LASTEXITCODE -ne 0) { throw 'Release creation failed.' }
