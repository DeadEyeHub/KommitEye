# Run as administrator after extracting the ZIP into a permanent program directory.
param(
    [int]$Port = 5080,
    [string]$ConfigPath = (Join-Path $env:ProgramData 'KommitEye\Server\server.json'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'KommitEye\Server\data'),
    [string]$GitExecutable = (Get-Command git.exe -ErrorAction SilentlyContinue).Source
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'KommitEye.Server.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Server executable missing: $exe" }
if (Get-Service KommitEye -ErrorAction SilentlyContinue) { throw 'The service already exists. Preserve its identity and config; use MSI maintenance for MSI installations.' }
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Invalid TCP port.' }
foreach ($path in @($ConfigPath)) {
    if (!$path -or ![IO.Path]::IsPathRooted($path) -or $path.Contains('"')) { throw 'Use absolute paths without quotes.' }
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$configDirectory = Split-Path $ConfigPath -Parent
if (!(Test-Path -LiteralPath $ConfigPath)) {
    foreach ($path in @($DataDirectory, $GitExecutable)) {
        if (!$path -or ![IO.Path]::IsPathRooted($path) -or $path.Contains('"')) { throw 'Use absolute data and Git paths without quotes.' }
    }
    & $exe --initialize-config --config $ConfigPath --data-dir $DataDirectory --urls "http://127.0.0.1:$Port" --git-path $GitExecutable --credential-protection LocalMachine
    if ($LASTEXITCODE -ne 0) { throw "Server configuration initialization failed: $ConfigPath" }
}
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($config.CredentialProtection -ne 'LocalMachine') { throw 'New virtual services require LocalMachine protection. Existing CurrentUser data requires a separate credential migration.' }
if (!$config.DataDirectory -or ![IO.Path]::IsPathRooted($config.DataDirectory) -or $config.DataDirectory.Contains('"')) { throw 'DataDirectory must be absolute.' }
$data = [IO.Path]::GetFullPath($config.DataDirectory).TrimEnd('\')
if ($configDirectory.Equals($data, 'OrdinalIgnoreCase') -or $configDirectory.StartsWith($data + '\', 'OrdinalIgnoreCase')) { throw 'Configuration cannot be inside the writable data folder.' }
$program = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
foreach ($directory in @($data, $configDirectory)) {
    if ($directory -eq [IO.Path]::GetPathRoot($directory) -or $directory.StartsWith('\\') -or
        $directory.Equals($program, 'OrdinalIgnoreCase') -or $directory.StartsWith($program + '\', 'OrdinalIgnoreCase') -or $program.StartsWith($directory.TrimEnd('\') + '\', 'OrdinalIgnoreCase')) { throw 'Use dedicated local configuration/data folders separate from the program folder.' }
    if ((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Configuration and data folders cannot be links.' }
}
if (!(Test-Path -LiteralPath $config.GitExecutable -PathType Leaf) -or [IO.Path]::GetFileName($config.GitExecutable) -ne 'git.exe') { throw 'Select an installed Git for Windows executable.' }
$serviceSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-80-3680015686-339733092-3489414343-1427611537-2043285906')
function Set-ServiceDirectoryAcl([string]$Path, [Security.AccessControl.FileSystemRights]$Rights) {
    [void][IO.Directory]::CreateDirectory($Path)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, $Rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Set-ServiceDirectoryAcl $configDirectory ReadAndExecute
Set-ServiceDirectoryAcl $data FullControl
# Give the service read/execute access to the extracted program, preserving other program ACLs.
& icacls.exe $PSScriptRoot /grant ('*{0}:(OI)(CI)(RX)' -f $serviceSid.Value)
if ($LASTEXITCODE -ne 0) { throw 'Cannot grant access to the program directory.' }
$credential = [pscredential]::new('NT SERVICE\KommitEye', [Security.SecureString]::new())
New-Service -Name KommitEye -DisplayName 'KommitEye Review Server' -StartupType Automatic -Credential $credential -BinaryPathName ('"{0}" --config "{1}"' -f $exe, $ConfigPath)
Write-Host "Service installed. Configuration: $ConfigPath. Grant repository access to NT SERVICE\KommitEye, then run Start-Service KommitEye."
