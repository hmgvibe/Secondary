$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$directory = Join-Path $projectRoot '.tools\monitoring'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$target = Join-Path $directory 'PresentMon-2.6.0-x64.exe'
$expected = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
if (Test-Path -LiteralPath $target) {
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $expected) { Write-Output $target; return }
    throw 'Existing PresentMon does not match the verified release; it has been preserved.'
}
$candidate = Join-Path $directory ('PresentMon-download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
Invoke-WebRequest -Uri 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' -OutFile $candidate
if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $expected) { throw 'Download hash mismatch. Candidate preserved for inspection; it will not be executed.' }
Move-Item -LiteralPath $candidate -Destination $target
Write-Output $target
