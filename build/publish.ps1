param(
    [ValidateSet('All','WebServer','Desktop')][string]$Product = 'All',
    [string]$Pnpm = 'pnpm',
    [switch]$SkipWebBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$desktopVersion = [string]$props.Project.PropertyGroup.Version
$webServerVersion = [string]$props.Project.PropertyGroup.WebServerVersion
$commit = (git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
$artifacts = Join-Path $repo 'artifacts'
$stage = Join-Path $artifacts ('stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
$products = if ($Product -eq 'All') { @('WebServer','Desktop') } else { @($Product) }
if ('WebServer' -in $products -and !$SkipWebBuild) {
    Push-Location (Join-Path $repo 'src/KommitEye.Web')
    try {
        & $Pnpm install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw 'Web dependency restore failed.' }
        & $Pnpm build
        if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
    } finally { Pop-Location }
}
$files = @()
foreach ($item in $products) {
    $version = if ($item -eq 'Desktop') { $desktopVersion } else { $webServerVersion }
    $destination = Join-Path $stage $item
    New-Item -ItemType Directory -Force $destination | Out-Null
    if ($item -eq 'Desktop') {
        & (Join-Path $repo 'KommitEye/publish-win-x64.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
        $exe = Join-Path $repo "KommitEye/dist/KommitEye-$version-win-x64.exe"
        $desktopOutput = Join-Path $artifacts "KommitEye-Desktop-$version-win-x64.exe"
        Copy-Item -LiteralPath $exe -Destination $desktopOutput -Force
        $files += $desktopOutput
        continue
    } else {
        dotnet publish (Join-Path $repo 'src/KommitEye.Server/KommitEye.Server.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -o $destination
        if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
        Copy-Item -Path (Join-Path $repo 'build/package/*') -Destination $destination
        if ($item -eq 'WebServer') {
            New-Item -ItemType Directory -Force (Join-Path $destination 'wwwroot') | Out-Null
            Copy-Item -Path (Join-Path $repo 'src/KommitEye.Web/dist/*') -Destination (Join-Path $destination 'wwwroot') -Recurse
        }
        $name = "KommitEye-$item-$version-win-x64.zip"
    }
    @{ version = $version; product = $item; commit = $commit; builtAt = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'build-info.json') -Encoding utf8
    $zip = Join-Path $artifacts $name
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -Force
    $files += $zip
    if ($item -eq 'WebServer') {
        & (Join-Path $PSScriptRoot 'publish-installer.ps1') -PayloadDirectory $destination
        if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
        $files += Join-Path $artifacts "KommitEye-WebServer-$version-win-x64.msi"
    }
}
$lines = foreach ($file in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf) }
$lines | Set-Content -LiteralPath (Join-Path $artifacts "SHA256SUMS-$Product.txt") -Encoding utf8
Write-Output $files
