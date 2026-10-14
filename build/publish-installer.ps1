param([Parameter(Mandatory)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $payload 'wwwroot/index.html'))) { throw 'The installer requires the built web UI.' }
dotnet build (Join-Path $PSScriptRoot 'installer/KommitEye.Installer.wixproj') -c Release -m:1 -p:UseSharedCompilation=false "-p:PayloadDir=$payload"
if ($LASTEXITCODE -ne 0) { throw 'WiX installer build failed.' }
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$name = "KommitEye-WebServer-$($props.Project.PropertyGroup.WebServerVersion)-win-x64.msi"
$built = Join-Path $PSScriptRoot "installer/bin/Release/$name"
if (!(Test-Path -LiteralPath $built)) { throw "Installer was not produced: $built" }
$destination = Join-Path $repo "artifacts/$name"
Copy-Item -LiteralPath $built -Destination $destination -Force
& (Join-Path $PSScriptRoot 'verify-installer.ps1') -Path $destination
if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed.' }
& (Join-Path $PSScriptRoot 'test-installer-preflight.ps1') -Path $destination
if ($LASTEXITCODE -ne 0) { throw 'Installer preflight checks failed.' }
Write-Output $destination
