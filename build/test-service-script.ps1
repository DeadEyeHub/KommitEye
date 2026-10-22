# Exercise the ZIP installer with SCM and ACL mutations replaced by recording stubs.
param([string]$ServerExecutable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('kommiteye-service-script-' + [guid]::NewGuid().ToString('N'))
$program = Join-Path $fixture 'program'
$configPath = Join-Path $fixture 'config/server.json'
$git = (Get-Command git.exe).Source
$global:kommitEyeFixtureInstalled = $null
$global:kommitEyeFixtureAccess = @{}
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; Write-Host "PASS: $Message" }
function Get-Service { param($Name, $ErrorAction) return $null }
function New-Service { param($Name, $DisplayName, $StartupType, $Credential, $BinaryPathName) $global:kommitEyeFixtureInstalled = @{ Credential = $Credential; Command = $BinaryPathName; Name = $Name } }
function Set-Acl { param($LiteralPath, $AclObject) $global:kommitEyeFixtureAccess[$LiteralPath] = $AclObject }
function icacls.exe { $global:LASTEXITCODE = 0 }
try {
    [void][IO.Directory]::CreateDirectory($program)
    Copy-Item (Join-Path $repo 'build/package/Install-Service.ps1') $program
    if (!$ServerExecutable) { $ServerExecutable = Join-Path $repo 'src/KommitEye.Server/bin/Release/net10.0/KommitEye.Server.exe' }
    if (!(Test-Path -LiteralPath $ServerExecutable)) { throw "Build the server first or pass -ServerExecutable: $ServerExecutable" }
    Copy-Item -Path (Join-Path (Split-Path $ServerExecutable -Parent) '*') -Destination $program -Recurse -Force
    & (Join-Path $program 'Install-Service.ps1') -ConfigPath $configPath -DataDirectory (Join-Path $fixture 'data') -GitExecutable $git
    $original = [IO.File]::ReadAllText($configPath)
    $config = $original | ConvertFrom-Json
    Assert ($config.CredentialProtection -eq 'LocalMachine') 'ZIP installer selects machine credential protection'
    Assert ($global:kommitEyeFixtureInstalled.Credential.UserName -eq 'NT SERVICE\KommitEye' -and $global:kommitEyeFixtureInstalled.Credential.Password.Length -eq 0) 'ZIP service registration uses virtual account and empty password'
    Assert ($global:kommitEyeFixtureInstalled.Command.Contains('--config "' + $configPath + '"')) 'ZIP registration quotes the absolute config path'
    $configAcl = $global:kommitEyeFixtureAccess[(Split-Path $configPath -Parent)]
    $dataAcl = $global:kommitEyeFixtureAccess[$config.DataDirectory]
    $sid = 'S-1-5-80-3680015686-339733092-3489414343-1427611537-2043285906'
    $configRule = $configAcl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference.Value -eq $sid }
    $dataRule = $dataAcl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference.Value -eq $sid }
    Assert ($configAcl.AreAccessRulesProtected -and ($configRule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute -and ($configRule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq 0) 'configuration ACL restricts service to read and execute'
    Assert ($dataAcl.AreAccessRulesProtected -and $dataRule.FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl) 'data ACL grants service write access with inheritance disabled'
    & (Join-Path $program 'Install-Service.ps1') -ConfigPath $configPath -DataDirectory (Join-Path $fixture 'other') -GitExecutable ''
    Assert ([IO.File]::ReadAllText($configPath) -eq $original) 'ZIP installation preserves existing configuration and uses its Git path'
} finally {
    Remove-Variable kommitEyeFixtureInstalled,kommitEyeFixtureAccess -Scope Global -ErrorAction SilentlyContinue
    if (([IO.Path]::GetFullPath($fixture)).StartsWith([IO.Path]::GetTempPath(), 'OrdinalIgnoreCase')) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
