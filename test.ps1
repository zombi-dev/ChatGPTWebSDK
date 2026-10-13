param([string]$BuildRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    node --test tests/automation/*.test.cjs tests/automation/*.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Workflow and monitor tests failed.' }
    node --test extensions/chatgpt-auth/tests/*.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
    $taskArguments = @()
    if ($BuildRoot) { $taskArguments = @('-p:ChatGPTWebBuildRoot=' + [IO.Path]::GetFullPath($BuildRoot)) }
    dotnet test tests/ChatGPTWebSdk.Tests/ChatGPTWebSdk.Tests.csproj -c Release --nologo --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=tests.trx' --results-directory artifacts/tests @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'SDK tests failed.' }
    [xml]$taskResults = Get-Content -LiteralPath artifacts/tests/tests.trx -Raw
    if ([int]$taskResults.TestRun.ResultSummary.Counters.passed -lt 1639) { throw 'The SDK test gate requires at least 1639 passing cases, including document/ZIP uploads, file inputs, ownership and history, HTTP Sentinel, MCP, OAuth, browser cleanup and actual example checks.' }
    dotnet test tests/ChatGPTWebSdk.Native.Tests/ChatGPTWebSdk.Native.Tests.csproj -c Release --nologo --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=native-tests.trx' --results-directory artifacts/tests @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'Native SDK integration tests failed.' }
    [xml]$taskNativeResults = Get-Content -LiteralPath artifacts/tests/native-tests.trx -Raw
    if ([int]$taskNativeResults.TestRun.ResultSummary.Counters.passed -lt 26) { throw 'The native SDK gate requires at least 26 passing cases using the real official NuGet assembly across all three backends, file inputs with SSE, native HTTP Sentinel and every public signature.' }
}
finally { Pop-Location }
