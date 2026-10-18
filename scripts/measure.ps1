param(
    [ValidateSet('Home','HomeWidgets','Shortcuts','GameLow','GameHigh','GameSensors','Hidden','Minimized','Image','Video')][string]$Mode = 'GameLow',
    [string]$Report = ('artifacts/performance/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-performance.json'),
    [ValidateRange(5,86400)][int]$Seconds = 60,
    [ValidateRange(0,300)][int]$Warmup = 10,
    [string]$Media,
    [string]$Executable
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$appExecutable = if ($Executable) { [IO.Path]::GetFullPath((Join-Path $projectRoot $Executable)) } else { Join-Path $projectRoot 'src\Secondary.App\bin\Release\net10.0-windows10.0.22621.0\Secondary.App.exe' }
if (-not $appExecutable.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Measurement executable must be inside the project.' }
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) { throw 'Build the Release application first or specify its candidate executable.' }
$reportPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Report))
if (-not $reportPath.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Report must be inside the project.' }
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a fresh report filename.' }
$cursor = Split-Path $reportPath -Parent
while ($cursor -and $cursor.StartsWith($projectRoot,[StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Report cannot traverse a reparse point.' }
    $cursor = Split-Path $cursor -Parent
}
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools\dotnet'
$info = [Diagnostics.ProcessStartInfo]::new($appExecutable)
$info.UseShellExecute = $false
$info.WorkingDirectory = $projectRoot
foreach ($argument in @('--measure',$reportPath,$Mode,"--seconds=$Seconds","--warmup=$Warmup")) { $info.ArgumentList.Add($argument) }
if ($Media) { $info.ArgumentList.Add('--media=' + [IO.Path]::GetFullPath((Join-Path $projectRoot $Media))) }
# Intentional interactive measurement window; settings are isolated in the application's benchmark mode.
$process = [Diagnostics.Process]::Start($info)
try {
    if (-not $process.WaitForExit(($Seconds + $Warmup + 45) * 1000)) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        throw 'Measurement exceeded its deadline; the owned measurement process was stopped.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw 'Measurement did not produce a valid report.' }
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $result.ValidRun) { throw "The report records an invalid run: $reportPath" }
    Write-Output $reportPath
} finally { $process.Dispose() }
