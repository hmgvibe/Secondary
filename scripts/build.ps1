param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $projectRoot
try {
    & $sdk build Secondary.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $sdk run --project tests/Secondary.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $sdk run --project tests/Secondary.UiTests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
} finally { Pop-Location }
