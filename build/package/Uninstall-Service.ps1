# Run from an elevated terminal. Data and configuration are preserved.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'KommitEye.Server.exe') --uninstall-service
if ($LASTEXITCODE -ne 0) { throw 'Service removal failed. See the executable diagnostic above.' }
