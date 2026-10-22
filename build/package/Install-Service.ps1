# Run from an elevated terminal after extracting the ZIP to a permanent directory.
param(
    [string]$ConfigPath = (Join-Path $env:ProgramData 'KommitEye\Server\server.json'),
    [ValidateSet('automatic','manual')][string]$Startup = 'automatic'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'KommitEye.Server.exe') --install-service --config $ConfigPath --service-start $Startup
if ($LASTEXITCODE -ne 0) { throw 'Service registration failed. See the executable diagnostic above.' }
