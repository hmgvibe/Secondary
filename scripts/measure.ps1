param([ValidateSet('Full','Light','Game','GameAfterFull')][string]$Mode = 'Game', [string]$Report = 'artifacts/stage-4/game-performance.json')
$ErrorActionPreference = 'Stop'
throw 'Redesign stage 1 is a static preview. Legacy telemetry benchmarking is disabled; no valid performance report can be produced.'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $projectRoot 'src\Secondary.App\bin\Release\net10.0-windows10.0.22621.0\Secondary.App.exe'
$reportPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Report))
if (-not $reportPath.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Report must be inside project.' }
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a fresh report filename to preserve previous measurements.' }
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools\dotnet'
# Intentional visible app: measure a rendered window, not a hidden test model.
Start-Process -FilePath $executable -ArgumentList @('--measure', ('"' + $reportPath + '"'), $Mode) -PassThru | Select-Object Id
