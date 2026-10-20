param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if ($sourcePath.TrimEnd('\') -eq $destinationPath.TrimEnd('\')) { throw 'Source and destination must differ.' }
if (Test-Path -LiteralPath $destinationPath) {
    if (@(Get-ChildItem -LiteralPath $destinationPath -Force).Count -gt 0) { throw 'Use a new, empty destination directory.' }
}
New-Item -ItemType Directory -Force $destinationPath | Out-Null
# SQLite files must be copied together while the source application is stopped.
# Hold exclusive handles until both the database and any uncheckpointed WAL are copied.
$reportStreams = @{}
try {
    foreach ($name in @('reports.db', 'reports.db-wal')) {
        $path = Join-Path $sourcePath $name
        if (Test-Path -LiteralPath $path) {
            if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked data path: $name"
            }
            try { $reportStreams[$name] = [IO.File]::Open($path, 'Open', 'Read', 'None') }
            catch { throw "Stop the source KommitEye application/service before importing SQLite reports. $($_.Exception.Message)" }
        }
    }
    foreach ($name in $reportStreams.Keys) {
        $output = [IO.File]::Create((Join-Path $destinationPath $name))
        try { $reportStreams[$name].CopyTo($output) }
        finally { $output.Dispose() }
    }
}
finally { foreach ($stream in $reportStreams.Values) { $stream.Dispose() } }
# Copy only application data. The source is preserved as the migration backup.
$names = @('projects.json','models.conf','settings.conf','state.json','mail-settings.json','mail-settings.json.smtp','mail-settings.json.gmail','mail-settings.json.outlook','mail-queue.json','token-usage.json','system-prompt.custom.en.txt','system-prompt.custom.ru.txt','reports','logs','oauth')
foreach ($name in $names) {
    $path = Join-Path $sourcePath $name
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked data path: $name" }
        if ($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $path -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw "Refusing linked content: $name" }
        Copy-Item -LiteralPath $path -Destination $destinationPath -Recurse
    }
}
Write-Host 'Import complete. Original data preserved. Run the server under the same Windows user to reuse DPAPI secrets, or reconnect mail.'
