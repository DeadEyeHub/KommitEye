$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "KommitEye.csproj"
[xml]$project = Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props')
$version = $project.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $version) {
    throw "The project version is not defined."
}

$outputDirectory = Join-Path $PSScriptRoot "dist"
$assemblyName = "KommitEye-$version-win-x64"
$executablePath = Join-Path $outputDirectory "$assemblyName.exe"
$publishDirectory = Join-Path $outputDirectory ('stage-' + [guid]::NewGuid().ToString('N'))

& dotnet publish `
    $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
Copy-Item -LiteralPath (Join-Path $publishDirectory 'KommitEye.exe') -Destination $executablePath -Force
if (-not (Test-Path $executablePath)) {
    throw "The published executable was not created."
}

Write-Host "Published: $executablePath"
