param([string]$BuildRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $taskBuildArguments = @()
    if ($BuildRoot) { $taskBuildArguments = @('-p:ChatGPTWebBuildRoot=' + [IO.Path]::GetFullPath($BuildRoot)) }
    $taskVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
    if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part release version.' }
    node --test extensions/chatgpt-auth/tests/*.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
    node extensions/chatgpt-auth/build.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension build failed.' }
    dotnet build ChatGPTWebSdk.sln -c Release --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test ChatGPTWebSdk.sln -c Release --no-build --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet pack src/ChatGPTWebSdk/ChatGPTWebSdk.csproj -c Release --no-build -o artifacts/packages --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    dotnet pack src/ChatGPTWebSdk.Browser/ChatGPTWebSdk.Browser.csproj -c Release --no-build -o artifacts/packages --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Browser package failed.' }
    dotnet pack src/ChatGPTWebSdk.OpenAI/ChatGPTWebSdk.OpenAI.csproj -c Release --no-build -o artifacts/packages --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'OpenAI replacement package failed.' }
    dotnet publish src/ChatGPTWebSdk.Proxy/ChatGPTWebSdk.Proxy.csproj -c Release --no-build -o artifacts/proxy --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Proxy publication failed.' }
    dotnet publish src/ChatGPTWebSdk.OpenAI/ChatGPTWebSdk.OpenAI.csproj -c Release --no-build -o artifacts/sdk --nologo @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw 'SDK DLL publication failed.' }
    Copy-Item -LiteralPath README.md -Destination artifacts/sdk/README.md
    Copy-Item -LiteralPath LICENSE -Destination artifacts/sdk/LICENSE
    Copy-Item -LiteralPath third_party/openai-dotnet/LICENSE -Destination artifacts/sdk/LICENSE.openai-dotnet
    New-Item -ItemType Directory -Path artifacts/release -Force | Out-Null
    foreach ($taskPackage in @('ChatGPTWebSdk', 'ChatGPTWebSdk.Browser', 'ChatGPTWebSdk.OpenAI')) {
        Copy-Item -LiteralPath "artifacts/packages/$taskPackage.$taskVersion.nupkg" -Destination artifacts/release
    }
    foreach ($taskExtension in @("chatgpt-web-sdk-auth-chromium-$taskVersion.zip", "chatgpt-web-sdk-auth-firefox-$taskVersion.zip", "chatgpt-web-sdk-auth-firefox-$taskVersion.xpi")) {
        Copy-Item -LiteralPath "artifacts/extensions/$taskExtension" -Destination artifacts/release
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($taskArchive in @(@('sdk', "ChatGPTWebSdk-DLLs-$taskVersion.zip"), @('proxy', "ChatGPTWebSdk-Proxy-$taskVersion.zip"))) {
        $taskZipPath = Join-Path $PSScriptRoot ('artifacts/release/' + $taskArchive[1])
        if (Test-Path -LiteralPath $taskZipPath) { Remove-Item -LiteralPath $taskZipPath }
        [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $PSScriptRoot ('artifacts/' + $taskArchive[0])), $taskZipPath)
    }
    $taskChecksumLines = Get-ChildItem -LiteralPath artifacts/release -File |
        Where-Object { $_.Extension -in @('.nupkg', '.zip', '.xpi') -and $_.Name.Contains($taskVersion) } |
        Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name }
    [IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'artifacts/release/SHA256SUMS.txt'), [string[]]$taskChecksumLines)
    Write-Host "Release $taskVersion built in artifacts/release."
}
finally {
    Pop-Location
}

