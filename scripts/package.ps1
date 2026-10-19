param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.2.0',
    [ValidateSet('Bundled','Installed')][string]$RuntimeMode = 'Bundled',
    [string]$OutputRoot = 'artifacts/release',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))
if (-not $releaseRoot.StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be inside the project.' }
$cursor = $releaseRoot
while ($cursor -and $cursor.StartsWith($projectRoot,[StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Package output cannot traverse a reparse point.' }
    $cursor = Split-Path $cursor -Parent
}
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$name = "Secondary-v$Version-windows-x64" + $(if ($RuntimeMode -eq 'Installed') {'-requires-runtime'} else {''})
$staging = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging $name
$finalDir = Join-Path $releaseRoot $name
$finalZip = Join-Path $releaseRoot "$name.zip"
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$manifestPath = Join-Path $staging 'versioned.manifest'
$manifest = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\Secondary.App\app.manifest') -Raw)
$manifest.assembly.assemblyIdentity.version = "$Version.0"
$manifest.Save($manifestPath)
$selfContained = $(if ($RuntimeMode -eq 'Bundled') {'true'} else {'false'})
$properties = @("-p:Version=$Version",'-p:PublishSingleFile=false','-p:PublishReadyToRun=false','-p:RestoreLockedMode=true','-m:1')
if ($Offline) { $properties += '-p:NuGetAudit=false'; Write-Warning 'Offline package: dependency vulnerability auditing is NOT completed.' }
else { $properties += @('-p:NuGetAudit=true','-p:NuGetAuditMode=all') }
Push-Location $projectRoot
try {
    & $sdk publish src/Secondary.App/Secondary.App.csproj -c Release -r win-x64 --self-contained $selfContained -o $publish --nologo @properties '-p:PublishSensorHelperSeparately=true' "-p:ApplicationManifest=$manifestPath"
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed; any previous package remains intact.' }
    & $sdk publish src/Secondary.SensorHelper/Secondary.SensorHelper.csproj -c Release -r win-x64 --self-contained $selfContained -o (Join-Path $publish 'SensorHelper') --nologo @properties
    if ($LASTEXITCODE -ne 0) { throw 'Sensor helper publish failed; any previous package remains intact.' }
    $required = @('Secondary.App.exe','SensorHelper\Secondary.SensorHelper.exe','Monitoring\PresentMon-2.6.0-x64.exe','Monitoring\PresentMon-LICENSE.txt','SensorHelper\Licenses\LICENSE','SensorHelper\Licenses\SOURCE.md','SensorHelper\Licenses\THIRD-PARTY-NOTICES.txt')
    foreach ($file in $required) { if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "Missing package component: $file" } }
    $presentMon = Join-Path $publish 'Monitoring\PresentMon-2.6.0-x64.exe'
    if ((Get-FileHash -LiteralPath $presentMon -Algorithm SHA256).Hash -ne 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF') { throw 'Published PresentMon hash differs.' }
    foreach ($file in @('Secondary.App.exe','SensorHelper\Secondary.SensorHelper.exe')) {
        if ((Get-Item -LiteralPath (Join-Path $publish $file)).VersionInfo.FileVersion -ne "$Version.0") { throw "Inconsistent file version: $file" }
    }
    $localRuntime = Join-Path $projectRoot '.tools\dotnet'
    if (Test-Path -LiteralPath $localRuntime) { $env:DOTNET_ROOT = $localRuntime }
    $smokePath = Join-Path $staging 'startup-check.json'
    $smokeInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $publish 'Secondary.App.exe'))
    $smokeInfo.UseShellExecute = $false
    $smokeInfo.WorkingDirectory = $publish
    foreach ($arg in @('--measure',$smokePath,'Home','--seconds=5','--warmup=1')) { $smokeInfo.ArgumentList.Add($arg) }
    # This intentional WPF test window uses an isolated profile and closes itself.
    $smoke = [Diagnostics.Process]::Start($smokeInfo)
    try {
        if (-not $smoke.WaitForExit(30000)) { $smoke.Kill($true); $null = $smoke.WaitForExit(5000); throw 'Candidate startup exceeded its deadline.' }
        if ($smoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $smokePath)) { throw 'Candidate did not start and close cleanly.' }
        $startup = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
        if (-not $startup.ValidRun -or ($startup.Version -split '\+')[0] -ne $Version) { throw 'Candidate startup scenario or runtime version is invalid.' }
    } finally { $smoke.Dispose() }
    $readme = [IO.File]::ReadAllText((Join-Path $projectRoot 'README.md')) -replace 'Secondary v\d+\.\d+\.\d+',"Secondary v$Version" -replace 'Secondary-v\d+\.\d+\.\d+-windows-x64.zip',"$name.zip"
    if ($RuntimeMode -eq 'Installed') { $readme = '此輕量發行包需要先安裝 .NET 10 Desktop Runtime x64。' + [Environment]::NewLine + [Environment]::NewLine + $readme.Replace('發行包已包含所需的 .NET 執行環境。','此包使用已安裝的 .NET 10 Desktop Runtime x64。') }
    [IO.File]::WriteAllText((Join-Path $publish 'README.md'),$readme,[Text.UTF8Encoding]::new($false))
    [ordered]@{ Version=$Version; RuntimeMode=$RuntimeMode; DependencyAuditCompleted=(-not $Offline); StartupPassed=$true; PresentMonSha256=(Get-FileHash -LiteralPath $presentMon -Algorithm SHA256).Hash; CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'package-info.json') -Encoding utf8
    $zip = Join-Path $staging "$name.zip"
    Compress-Archive -LiteralPath $publish -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashFile = Join-Path $releaseRoot 'SHA256SUMS.txt'
    $hashLines = if (Test-Path -LiteralPath $hashFile) { @(Get-Content -LiteralPath $hashFile | Where-Object { $_ -notmatch ('  ' + [regex]::Escape("$name.zip") + '$') }) } else { @() }
    $stagedHash = Join-Path $staging 'SHA256SUMS.txt'
    @($hashLines) + "$hash  $name.zip" | Set-Content -LiteralPath $stagedHash -Encoding ascii
    $backup = '.previous-' + [Guid]::NewGuid().ToString('N')
    $promotions = @(@{Candidate=$publish; Target=$finalDir}, @{Candidate=$zip; Target=$finalZip}, @{Candidate=$stagedHash; Target=$hashFile})
    $moved = [Collections.Generic.List[object]]::new()
    $saved = [Collections.Generic.List[object]]::new()
    # Preserve previous files and reverse each completed promotion if any later step fails.
    try {
        foreach ($item in $promotions) {
            if (Test-Path -LiteralPath $item.Target) {
                Move-Item -LiteralPath $item.Target -Destination ($item.Target + $backup)
                $saved.Add($item)
            }
            Move-Item -LiteralPath $item.Candidate -Destination $item.Target
            $moved.Add($item)
        }
    } catch {
        $promotionError = $_
        for ($i=$moved.Count-1; $i -ge 0; $i--) {
            $item = $moved[$i]
            try { Move-Item -LiteralPath $item.Target -Destination $item.Candidate } catch { Write-Warning "Candidate recovery failed: $($item.Target)" }
        }
        for ($i=$saved.Count-1; $i -ge 0; $i--) {
            $item = $saved[$i]
            if (Test-Path -LiteralPath $item.Target) { Write-Warning "Previous package retained at backup because its target is occupied: $($item.Target + $backup)"; continue }
            try { Move-Item -LiteralPath ($item.Target + $backup) -Destination $item.Target } catch { Write-Warning "Previous package recovery failed: $($item.Target + $backup)" }
        }
        throw $promotionError
    }
    Write-Output $finalZip
    Write-Output $hashFile
} finally { Pop-Location }
