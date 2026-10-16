param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Offline,
    [switch]$Live,
    [switch]$Sensors,
    [switch]$GameTelemetry,
    [string]$Video,
    [string]$OutputDirectory = 'artifacts/verification'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$output = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $output.StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Verification output must be inside the project.' }
$cursor = $output
while ($cursor -and $cursor.StartsWith($projectRoot,[StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Verification output cannot traverse a reparse point.' }
    $cursor = Split-Path $cursor -Parent
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$receiptPath = Join-Path $output 'verification.json'
$receipt = [ordered]@{ Status='Running'; Configuration=$Configuration; DependencyAuditCompleted=$false; CorePassed=$false; UiPassed=$false; LiveRequested=[bool]$Live; SensorsRequested=[bool]$Sensors; GameTelemetryRequested=[bool]$GameTelemetry; VideoRequested=[bool]$Video; StartedUtc=[DateTimeOffset]::UtcNow.ToString('O') }
$receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$testTemporary = Join-Path $output ('.test-temp-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testTemporary -Force | Out-Null
$env:TEMP = $testTemporary
$env:TMP = $testTemporary
Push-Location $projectRoot
try {
    $restore = @('restore','Secondary.slnx','--locked-mode','-m:1')
    if ($Offline) { $restore += @('--ignore-failed-sources','-p:NuGetAudit=false'); Write-Warning 'Offline verification: dependency vulnerability auditing is NOT completed.' }
    else { $restore += @('-p:NuGetAudit=true','-p:NuGetAuditMode=all') }
    & $sdk @restore
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore or vulnerability auditing failed.' }
    $receipt.DependencyAuditCompleted = (-not $Offline)
    & $sdk build Secondary.slnx -c $Configuration --no-restore --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $sdk run --project tests/Secondary.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    $receipt.CorePassed = $true
    $uiArgs = @($output)
    if ($Live) { $uiArgs += '--live' }
    if ($Sensors) { $uiArgs += '--sensors' }
    if ($GameTelemetry) { $uiArgs += '--game-telemetry' }
    if ($Video) {
        $videoPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Video))
        if (-not $Live -or -not (Test-Path -LiteralPath $videoPath -PathType Leaf)) { throw 'Video verification requires -Live and an existing local MP4.' }
        $uiArgs += '--video=' + $videoPath
    }
    & $sdk run --project tests/Secondary.UiTests -c $Configuration --no-build -- @uiArgs
    if ($LASTEXITCODE -ne 0) { throw 'UI or requested integration tests failed.' }
    $receipt.UiPassed = $true
    $receipt.Status = 'Passed'
    $receipt.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
} catch {
    $receipt.Status = 'Failed'
    $receipt.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
    throw
} finally { $env:TEMP = $previousTemp; $env:TMP = $previousTmp; Pop-Location }
