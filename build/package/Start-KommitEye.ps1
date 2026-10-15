param([string]$ConfigPath, [int]$Port = 5080, [string]$DataDirectory = (Join-Path $PSScriptRoot 'data'))
$ErrorActionPreference = 'Stop'
if ($ConfigPath) {
    & (Join-Path $PSScriptRoot 'KommitEye.Server.exe') --config ([IO.Path]::GetFullPath($ConfigPath))
    exit $LASTEXITCODE
}
$env:KOMMITEYE_DATA_DIR = [System.IO.Path]::GetFullPath($DataDirectory)
Write-Host "Open http://localhost:$Port on this computer, or http://<server-address>:$Port from the LAN."
& (Join-Path $PSScriptRoot 'KommitEye.Server.exe') --urls "http://0.0.0.0:$Port"
