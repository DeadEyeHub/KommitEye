param([Parameter(Mandatory)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $payload 'wwwroot/index.html'))) { throw 'The installer requires the built web UI.' }
# Stage the new executable/runtime inside the CA so upgrades can stop old services
# even when their executable predates the registration commands. No web UI is needed.
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'installer/obj') | Out-Null
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath (Join-Path $PSScriptRoot 'installer/obj/service-tools.zip') -Force
dotnet build (Join-Path $PSScriptRoot 'installer/KommitEye.Installer.wixproj') -c Release -m:1 -p:UseSharedCompilation=false "-p:PayloadDir=$payload"
if ($LASTEXITCODE -ne 0) { throw 'WiX installer build failed.' }
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$name = "KommitEye-Service-$($props.Project.PropertyGroup.Version)-win-x64.msi"
$built = Join-Path $PSScriptRoot "installer/bin/Release/$name"
if (!(Test-Path -LiteralPath $built)) { throw "Installer was not produced: $built" }
$destination = Join-Path $repo "artifacts/$name"
Copy-Item -LiteralPath $built -Destination $destination -Force
& (Join-Path $PSScriptRoot 'verify-installer.ps1') -Path $destination
if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed.' }
& (Join-Path $PSScriptRoot 'test-installer-preflight.ps1') -Path $destination
if ($LASTEXITCODE -ne 0) { throw 'Installer preflight checks failed.' }
Write-Output $destination
