param(
    [switch]$SkipWindows,
    [switch]$SkipMacOS
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Invoke-DotNetTest([string]$project) {
    & dotnet test (Join-Path $repositoryRoot $project) -c Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed for $project."
    }
}

Invoke-DotNetTest 'tests\RobloxAccountManager.Core.Tests\RobloxAccountManager.Core.Tests.csproj'
Invoke-DotNetTest 'tests\RobloxAccountManager.Desktop.Tests\RobloxAccountManager.Desktop.Tests.csproj'

if (-not $SkipMacOS) {
    Invoke-DotNetTest 'tests\RobloxAccountManager.Platform.MacOS.Tests\RobloxAccountManager.Platform.MacOS.Tests.csproj'
}

if (-not $SkipWindows -and $IsWindows) {
    Invoke-DotNetTest 'tests\RobloxAltClient.SmokeTests\RobloxAltClient.SmokeTests.csproj'
    & dotnet build (Join-Path $repositoryRoot 'src\RobloxAccountManager.Platform.Windows\RobloxAccountManager.Platform.Windows.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'The reusable Windows platform adapter did not build.'
    }
}

& dotnet build (Join-Path $repositoryRoot 'sdk\RobloxAccountManager.PluginSdk\RobloxAccountManager.PluginSdk.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'The plugin SDK did not build.'
}
