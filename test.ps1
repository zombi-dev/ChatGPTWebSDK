param([string]$BuildRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    node --test extensions/chatgpt-auth/tests/*.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
    $taskArguments = @()
    if ($BuildRoot) { $taskArguments = @('-p:ChatGPTWebBuildRoot=' + [IO.Path]::GetFullPath($BuildRoot)) }
    dotnet test tests/ChatGPTWebSdk.Tests/ChatGPTWebSdk.Tests.csproj -c Release --nologo --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=tests.trx' --results-directory artifacts/tests @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'SDK tests failed.' }
    [xml]$taskResults = Get-Content -LiteralPath artifacts/tests/tests.trx -Raw
    if ([int]$taskResults.TestRun.ResultSummary.Counters.passed -lt 1060) { throw 'The SDK test gate requires at least 1060 passing cases (115 scoped MCP cases beyond v1.2.0).' }
}
finally { Pop-Location }
