param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.0',
    [string]$OutputRoot = 'artifacts/release',
    [switch]$Offline
)
# Packages the connection helper (連接助手) for the Windows main computer: self-contained win-x64 with
# the verified PresentMon and sensor helper, started once in a throwaway profile before it is zipped.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))
if (-not $releaseRoot.StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be inside the project.' }
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$name = "Secondary-Connect-v$Version-windows-x64"
$staging = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging $name
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$manifestPath = Join-Path $staging 'versioned.manifest'
$manifest = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\Secondary.Connect\app.manifest') -Raw)
$manifest.assembly.assemblyIdentity.version = "$Version.0"
$manifest.Save($manifestPath)
$properties = @("-p:Version=$Version",'-p:PublishSingleFile=false','-p:PublishReadyToRun=false','-p:RestoreLockedMode=true','-m:1')
if ($Offline) { $properties += '-p:NuGetAudit=false'; Write-Warning 'Offline package: dependency vulnerability auditing is NOT completed.' }
else { $properties += @('-p:NuGetAudit=true','-p:NuGetAuditMode=all') }
Push-Location $projectRoot
try {
    & $sdk publish src/Secondary.Connect/Secondary.Connect.csproj -c Release -r win-x64 --self-contained true -o $publish --nologo @properties "-p:ApplicationManifest=$manifestPath"
    if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed; any previous package remains intact.' }
    & $sdk publish src/Secondary.SensorHelper/Secondary.SensorHelper.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $publish 'SensorHelper') --nologo @properties
    if ($LASTEXITCODE -ne 0) { throw 'Sensor helper publish failed; any previous package remains intact.' }
    $required = @('Secondary.Connect.exe','SensorHelper\Secondary.SensorHelper.exe','Monitoring\PresentMon-2.6.0-x64.exe','Monitoring\PresentMon-LICENSE.txt','SensorHelper\Licenses\LICENSE','SensorHelper\Licenses\SOURCE.md','SensorHelper\Licenses\THIRD-PARTY-NOTICES.txt')
    foreach ($file in $required) { if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "Missing package component: $file" } }
    $presentMon = Join-Path $publish 'Monitoring\PresentMon-2.6.0-x64.exe'
    if ((Get-FileHash -LiteralPath $presentMon -Algorithm SHA256).Hash -ne 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF') { throw 'Published PresentMon hash differs.' }
    foreach ($file in @('Secondary.Connect.exe','SensorHelper\Secondary.SensorHelper.exe')) {
        if ((Get-Item -LiteralPath (Join-Path $publish $file)).VersionInfo.FileVersion -ne "$Version.0") { throw "Inconsistent file version: $file" }
    }
    # The check builds the tray in a throwaway profile without listening, so a running helper is unaffected.
    $receipt = Join-Path $staging 'startup-check.json'
    $check = Start-Process -FilePath (Join-Path $publish 'Secondary.Connect.exe') -ArgumentList "--startup-check=`"$receipt`"" -WorkingDirectory $publish -PassThru
    if (-not $check.WaitForExit(30000)) { $check.Kill($true); throw 'Helper startup exceeded its deadline.' }
    if ($check.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $receipt)) { throw 'Helper did not start and close cleanly.' }
    $startup = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($startup.Status -ne 'Started' -or $startup.Version -ne "$Version.0") { throw 'Helper startup receipt is invalid.' }
    $zip = Join-Path $staging "$name.zip"
    Compress-Archive -LiteralPath $publish -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashFile = Join-Path $releaseRoot 'SHA256SUMS.txt'
    $hashLines = if (Test-Path -LiteralPath $hashFile) { @(Get-Content -LiteralPath $hashFile | Where-Object { $_ -notmatch ('  ' + [regex]::Escape("$name.zip") + '$') }) } else { @() }
    foreach ($target in @((Join-Path $releaseRoot $name), (Join-Path $releaseRoot "$name.zip"))) { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force } }
    Move-Item -LiteralPath $publish -Destination (Join-Path $releaseRoot $name)
    Move-Item -LiteralPath $zip -Destination (Join-Path $releaseRoot "$name.zip")
    @($hashLines) + "$hash  $name.zip" | Set-Content -LiteralPath $hashFile -Encoding ascii
    Write-Output (Join-Path $releaseRoot "$name.zip")
    Write-Output $hashFile
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
