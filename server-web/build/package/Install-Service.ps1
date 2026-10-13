# Run as administrator. Choose a service account that has repository and credential access.
param([int]$Port = 5080, [string]$DataDirectory = (Join-Path $PSScriptRoot 'data'), [Parameter(Mandatory)][pscredential]$Credential)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'KommitEye.Server.exe'
$data = [System.IO.Path]::GetFullPath($DataDirectory)
if ($data.Contains('"') -or $exe.Contains('"')) { throw 'Invalid path.' }
New-Service -Name KommitEye -DisplayName 'KommitEye Review Server' -StartupType Automatic -Credential $Credential -BinaryPathName ('"{0}" --data-dir "{1}" --urls "http://0.0.0.0:{2}"' -f $exe,$data,$Port)
Write-Host 'Service installed. Grant this account access to the data folder and repositories, then run Start-Service KommitEye.'
