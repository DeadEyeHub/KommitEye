param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$archive = (Resolve-Path -LiteralPath $Path).Path
$destination = Join-Path $repo ('artifacts/verify-service-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $destination
foreach ($required in @('KommitEye.Server.exe', 'KommitEye.Platform.dll', 'coreclr.dll', 'hostpolicy.dll', 'wwwroot/index.html', 'build-info.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $destination $required))) { throw "Package is missing $required" }
}
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$info = Get-Content -LiteralPath (Join-Path $destination 'build-info.json') -Raw | ConvertFrom-Json
if ($info.version -ne [string]$props.Project.PropertyGroup.Version -or $info.product -ne 'Service') {
    throw 'The package version or product does not match this checkout.'
}
Push-Location $repo
try {
    # Run the packaged apphost, including its bundled runtime, instead of dotnet <dll>.
    dotnet run --project checks/Server/Server.csproj -c Release -- (Join-Path $destination 'KommitEye.Server.exe') --service-config
    if ($LASTEXITCODE -ne 0) { throw 'Packaged server verification failed.' }
} finally { Pop-Location }
Write-Output "Verified Service ZIP: $archive"
