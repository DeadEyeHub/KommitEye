param(
    [string]$TemporaryDirectory = $env:RUNNER_TEMP,
    [string]$FixtureDirectory = (Join-Path $env:PUBLIC 'Documents'),
    [string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/msi-diagnostics')
)
$ErrorActionPreference = 'Stop'
# upload-artifact needs a single root, even when TEMP and Public are on different drives.
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
if ($TemporaryDirectory -and (Test-Path -LiteralPath $TemporaryDirectory)) {
    foreach ($file in Get-ChildItem -LiteralPath $TemporaryDirectory -File -Filter 'kommiteye-msi-check.log*') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $file.Name) -Force
    }
}
if (Test-Path -LiteralPath $FixtureDirectory) {
    foreach ($fixture in Get-ChildItem -LiteralPath $FixtureDirectory -Directory -Filter 'KommitEye MSI checks *') {
        $logs = @(Get-ChildItem -LiteralPath $fixture.FullName -File -Filter '*.log')
        if ($logs.Count -eq 0) { continue }
        $target = Join-Path $Destination $fixture.Name
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        foreach ($file in $logs) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force }
    }
}
