param([string]$SdkDirectory = 'artifacts/sdk', [string]$ProxyDirectory = 'artifacts/proxy', [string]$OutputDirectory = 'artifacts/release',
    [ValidateSet('All', 'Prepare', 'Verify', 'Archive')][string]$Stage = 'All')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSdk = [IO.Path]::GetFullPath((Join-Path $taskRoot $SdkDirectory))
$taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'VERSION') -Raw).Trim()
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid bundle version.' }
$taskPlatform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$taskPlatform += '-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ($taskPlatform -notin @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')) { throw "Unsupported browser bundle platform: $taskPlatform" }
$taskBundle = Join-Path $taskRoot "artifacts/bundles/$taskPlatform-$taskVersion"
if (!$taskBundle.StartsWith((Join-Path $taskRoot 'artifacts/bundles') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Bundle staging directory escaped the workspace.' }
$taskBrowsers = Join-Path $taskBundle 'browsers'
$taskManifestPath = Join-Path $taskBrowsers 'browser.json'
$taskPreparedFile = "$taskBundle.prepared.json"
$taskVerifiedFile = "$taskBundle.verified.json"
function Get-BundleFingerprint {
    $taskMetadata = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
    if ($taskMetadata.version -ne 1 -or $taskMetadata.platform -ne $taskPlatform) { throw 'Unexpected browser manifest.' }
    $taskNativeExecutable = [IO.Path]::GetFullPath((Join-Path $taskBrowsers $taskMetadata.relativeExecutable))
    if (!$taskNativeExecutable.StartsWith($taskBrowsers + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Browser executable escaped its bundle.' }
    return ([ordered]@{ version = $taskVersion; platform = $taskPlatform;
        manifest = (Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash;
        browser = (Get-FileHash -LiteralPath $taskNativeExecutable -Algorithm SHA256).Hash;
        sdk = (Get-FileHash -LiteralPath (Join-Path $taskBundle 'OpenAI.dll') -Algorithm SHA256).Hash } | ConvertTo-Json -Compress)
}
if ($Stage -in @('All', 'Prepare')) {
    foreach ($taskReceipt in @($taskPreparedFile, $taskVerifiedFile)) {
        if (Test-Path -LiteralPath $taskReceipt) { Remove-Item -LiteralPath $taskReceipt -Force }
    }
    if (Test-Path -LiteralPath $taskBundle) { Remove-Item -LiteralPath $taskBundle -Recurse -Force }
    New-Item -ItemType Directory -Path $taskBundle -Force | Out-Null
    Get-ChildItem -LiteralPath $taskSdk -Force | Where-Object Name -ne '.playwright' | Copy-Item -Destination $taskBundle -Recurse
    Copy-Item -LiteralPath (Join-Path $taskSdk '.playwright') -Destination (Join-Path $taskBundle '.playwright') -Recurse -Force
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath((Join-Path $taskRoot $ProxyDirectory))) -Destination (Join-Path $taskBundle 'proxy') -Recurse
    New-Item -ItemType Directory -Path (Join-Path $taskBundle 'example') -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $taskRoot 'examples/QuickStart') -File | Where-Object Extension -in @('.cs', '.csproj') |
        Copy-Item -Destination (Join-Path $taskBundle 'example')
    $taskNodePlatform = switch ($taskPlatform) { 'win-x64' { 'win32_x64' } 'osx-x64' { 'darwin-x64' } 'osx-arm64' { 'darwin-arm64' } 'linux-x64' { 'linux-x64' } }
    foreach ($taskDriverDirectory in @((Join-Path $taskBundle '.playwright/node'), (Join-Path $taskBundle 'proxy/.playwright/node'))) {
        Get-ChildItem -LiteralPath $taskDriverDirectory -Directory | Where-Object Name -ne $taskNodePlatform | ForEach-Object {
            $taskForeignDriver = [IO.Path]::GetFullPath($_.FullName)
            if (!$taskForeignDriver.StartsWith($taskDriverDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Driver cleanup escaped the bundle.' }
            Remove-Item -LiteralPath $taskForeignDriver -Recurse -Force
        }
    }
}
$taskOriginalBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
$taskOriginalSkipGc = $env:PLAYWRIGHT_SKIP_BROWSER_GC
try {
    $env:PLAYWRIGHT_BROWSERS_PATH = $taskBrowsers
    $env:PLAYWRIGHT_SKIP_BROWSER_GC = '1'
    if ($Stage -in @('All', 'Prepare')) {
        $taskCli = Join-Path $taskBundle '.playwright/package/cli.js'
        node $taskCli install chromium --no-shell
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Chromium download failed.' }
        $taskPackage = Join-Path $taskBundle '.playwright/package'
        $taskExecutable = node -e 'process.stdout.write(require(process.argv[1]).chromium.executablePath())' $taskPackage
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $taskExecutable)) { throw 'Downloaded browser executable is missing.' }
        $taskRelative = [IO.Path]::GetRelativePath($taskBrowsers, $taskExecutable).Replace('\', '/')
        if ($taskRelative.StartsWith('../')) { throw 'The downloaded browser escaped its bundle directory.' }
        $taskLinks = [IO.Path]::GetFullPath((Join-Path $taskBrowsers '.links'))
        if (!$taskLinks.StartsWith($taskBrowsers + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Browser cache link cleanup escaped the bundle.' }
        if (Test-Path -LiteralPath $taskLinks) { Remove-Item -LiteralPath $taskLinks -Recurse -Force }
        $taskBrowser = (Get-Content -LiteralPath (Join-Path $taskPackage 'browsers.json') -Raw | ConvertFrom-Json).browsers | Where-Object name -eq chromium
        $taskManifest = @{ version = 1; platform = $taskPlatform; relativeExecutable = $taskRelative; revision = $taskBrowser.revision; browserVersion = $taskBrowser.browserVersion }
        [IO.File]::WriteAllText($taskManifestPath, ($taskManifest | ConvertTo-Json))
        [IO.File]::WriteAllText($taskPreparedFile, (Get-BundleFingerprint))
        Write-Host "Prepared pinned Chromium for $taskPlatform."
    }
    if ($Stage -eq 'Prepare') { return }
    if (!(Test-Path -LiteralPath $taskPreparedFile)) { throw 'Prepare the browser bundle before verifying or archiving it.' }
    $taskFingerprint = Get-BundleFingerprint
    if ((Get-Content -LiteralPath $taskPreparedFile -Raw) -cne $taskFingerprint) { throw 'Prepared SDK or browser inputs changed.' }
    if ($Stage -in @('All', 'Verify')) {
        Write-Host "Building the bundled example for $taskPlatform."
        dotnet build (Join-Path $taskBundle 'example') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'The bundled example did not build.' }
        Write-Host "Launching the bundled example for native rendering verification."
        dotnet run --project (Join-Path $taskBundle 'example') -c Release --no-build --no-restore -- --verify-bundle
        if ($LASTEXITCODE -ne 0) { throw 'The bundled example, driver or browser failed its offline launch check.' }
        foreach ($taskGeneratedName in @('bin', 'obj')) {
            $taskGenerated = [IO.Path]::GetFullPath((Join-Path $taskBundle "example/$taskGeneratedName"))
            if (!$taskGenerated.StartsWith($taskBundle + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Example cleanup escaped its bundle directory.' }
            if (Test-Path -LiteralPath $taskGenerated) { Remove-Item -LiteralPath $taskGenerated -Recurse -Force }
        }
        [IO.File]::WriteAllText($taskVerifiedFile, $taskFingerprint)
        Write-Host "Verified the SDK and pinned browser for $taskPlatform."
    }
    if ($Stage -eq 'Verify') { return }
    if (!(Test-Path -LiteralPath $taskVerifiedFile) -or (Get-Content -LiteralPath $taskVerifiedFile -Raw) -cne $taskFingerprint) { throw 'The prepared browser bundle must pass verification before archiving.' }
    $taskBrowser = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
    [IO.File]::WriteAllText((Join-Path $taskBundle 'BROWSER-NOTICES.txt'), @"
This download includes Chrome for Testing (Chromium), revision $($taskBrowser.revision), browser version $($taskBrowser.browserVersion), distributed through Microsoft.Playwright 1.63.0.
Chromium third-party licenses are included inside its browser directory and available at chrome://credits.
Playwright's Apache 2.0 license and notices are in .playwright/package/LICENSE and NOTICE.
Node.js licenses are in .playwright/node/LICENSE. SDK and upstream OpenAI licenses are included at the bundle root.
Windows: extract this ZIP completely. Linux/macOS: extract the tar.gz with tar -xzf to preserve executable permissions and application symlinks.
Keep browsers/ beside the DLLs or set BrowserSentinelOptions.BundledBrowserDirectory. A proxy in proxy/ also discovers ../browsers/.
Linux requires the system libraries described in https://playwright.dev/dotnet/docs/browsers#install-system-dependencies.
"@)
    $taskArchive = Join-Path $taskOutput "ChatGPTWebSdk-Bundle-$taskPlatform-$taskVersion"
    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    if ($IsWindows) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (Test-Path -LiteralPath "$taskArchive.zip") { Remove-Item -LiteralPath "$taskArchive.zip" }
        [IO.Compression.ZipFile]::CreateFromDirectory($taskBundle, "$taskArchive.zip")
    }
    else {
        tar -czf "$taskArchive.tar.gz" -C $taskBundle .
        if ($LASTEXITCODE -ne 0) { throw 'Browser bundle archive failed.' }
    }
    Write-Host "Bundled Chromium $($taskBrowser.browserVersion) for $taskPlatform."
}
finally {
    $env:PLAYWRIGHT_BROWSERS_PATH = $taskOriginalBrowserPath
    $env:PLAYWRIGHT_SKIP_BROWSER_GC = $taskOriginalSkipGc
}
