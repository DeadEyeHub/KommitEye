# Real SCM regression check; run elevated on a Windows machine without KommitEye installed.
param([string]$ServerExecutable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$ServerExecutable) {
    $payload = Join-Path ([IO.Path]::GetTempPath()) ('kommiteye-scm-payload-' + [guid]::NewGuid().ToString('N'))
    dotnet publish (Join-Path $repo 'src/KommitEye.Server/KommitEye.Server.csproj') -c Release -r win-x64 --self-contained true -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Service fixture publish failed.' }
    $ServerExecutable = Join-Path $payload 'KommitEye.Server.exe'
}
$result = Join-Path ([IO.Path]::GetTempPath()) ('kommiteye-scm-' + [guid]::NewGuid().ToString('N') + '.log')
try { & (Join-Path $PSScriptRoot 'test-windows-service.ps1') -Payload (Split-Path $ServerExecutable -Parent) -Result $result }
finally { if (Test-Path $result) { Get-Content -LiteralPath $result } }
