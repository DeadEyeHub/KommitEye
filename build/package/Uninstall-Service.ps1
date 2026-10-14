$ErrorActionPreference = 'Stop'
Stop-Service -Name KommitEye -ErrorAction SilentlyContinue
sc.exe delete KommitEye
if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
Write-Host 'Service removed. Repository and application data were preserved.'
