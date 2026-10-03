param([string]$BuildRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $taskBuildArguments = @()
    if ($BuildRoot) { $taskBuildArguments = @('-p:ChatGPTWebBuildRoot=' + [IO.Path]::GetFullPath($BuildRoot)) }
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
}
finally {
    Pop-Location
}

