$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $projectRoot 'src\Secondary.App\bin\Release\net10.0-windows10.0.22621.0\Secondary.App.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Please run scripts/build.ps1 first.' }
$localRuntime = Join-Path $projectRoot '.tools\dotnet'
if (Test-Path -LiteralPath $localRuntime) { $env:DOTNET_ROOT = $localRuntime }
# A visible window is intentional: this is the interactive application launcher.
Start-Process -FilePath $executable
