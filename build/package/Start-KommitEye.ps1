param([string]$ConfigPath, [int]$Port, [string]$DataDirectory)
$ErrorActionPreference = 'Stop'
$arguments = @()
if ($ConfigPath) { $arguments += @('--config', [IO.Path]::GetFullPath($ConfigPath)) }
if ($PSBoundParameters.ContainsKey('Port')) { $arguments += @('--urls', "http://127.0.0.1:$Port") }
if ($DataDirectory) { $arguments += @('--data-dir', [IO.Path]::GetFullPath($DataDirectory)) }
& (Join-Path $PSScriptRoot 'KommitEye.Server.exe') @arguments
exit $LASTEXITCODE
