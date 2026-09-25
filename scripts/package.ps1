param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$packageName = "Secondary-v$Version-windows-x64"
$publishDir = Join-Path $releaseRoot $packageName
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$hashPath = Join-Path $releaseRoot 'SHA256SUMS.txt'

if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Push-Location $projectRoot
try {
    & $sdk publish src/Secondary.App/Secondary.App.csproj -c Release -r win-x64 --self-contained true -o $publishDir --nologo -p:Version=$Version -p:PublishSingleFile=false -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

    $helperDir = Join-Path $publishDir 'SensorHelper'
    & $sdk publish src/Secondary.SensorHelper/Secondary.SensorHelper.csproj -c Release -r win-x64 --self-contained true -o $helperDir --nologo -p:Version=$Version -p:PublishSingleFile=false -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { throw 'Sensor helper publish failed.' }

    Copy-Item -LiteralPath README.md -Destination (Join-Path $publishDir 'README.md')
    Compress-Archive -LiteralPath $publishDir -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $hashPath -Value "$hash  $packageName.zip" -Encoding ascii
    Write-Output $zipPath
    Write-Output $hashPath
} finally { Pop-Location }
