param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$fullPath = (Resolve-Path -LiteralPath $Path).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($fullPath, 0)
function Read-Rows([string]$Query, [int]$Columns = 1) {
    $view = $database.OpenView($Query)
    try {
        [void]$view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            $values = @()
            for ($column = 1; $column -le $Columns; $column++) { $values += $record.StringData($column) }
            Write-Output -NoEnumerate $values
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record)
        }
    } finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view) }
}
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; Write-Host "PASS: $Message" }
try {
    $properties = @{}
    foreach ($row in (Read-Rows 'SELECT `Property`, `Value` FROM `Property`' 2)) { $properties[$row[0]] = $row[1] }
    Assert ($properties['ProductName'] -eq 'KommitEye Service') 'MSI product identity'
    Assert ($properties['MSIRESTARTMANAGERCONTROL'] -eq 'DisableShutdown') 'Restart Manager detects locked files without automatically shutting down applications'
    Assert ($properties['MsiLogging'] -eq 'voicewarmup') 'installation and removal generate diagnostic logs'
    Assert ($properties['MsiHiddenProperties'].Split(';') -contains 'SERVICEPASSWORD') 'service password is hidden in MSI logging'
    Assert ($properties['SecureCustomProperties'].Split(';') -contains 'SERVICEPASSWORD') 'service password crosses elevation securely'
    Assert (!$properties.ContainsKey('SERVICEPASSWORD')) 'no account password is embedded in the MSI'
    $service = @(Read-Rows 'SELECT `Name`, `StartName`, `Password`, `Arguments` FROM `ServiceInstall`' 4)
    Assert ($service.Count -eq 1 -and $service[0][0] -eq 'KommitEye') 'one Windows service with the expected name'
    Assert ($service[0][1] -eq '[SERVICEACCOUNT]' -and $service[0][2] -eq '[SERVICEPASSWORD]') 'service uses the selected Windows account'
    Assert ($service[0][3] -eq '--config "[CONFIGFILE]"') 'service receives an explicit configuration file'
    $files = @(Read-Rows 'SELECT `FileName` FROM `File`') | ForEach-Object { $_[0].Split('|')[-1] }
    Assert ($files -contains 'KommitEye.Server.exe' -and $files -contains 'coreclr.dll') 'server and self-contained .NET runtime included'
    Assert ($files -contains 'index.html' -and @($files | Where-Object { $_ -like '*.js' }).Count -gt 0) 'compiled React UI included'
    Assert (@($files | Where-Object { $_ -like 'client_secret*' -or $_ -in @('admin.json','server.db','models.conf','settings.conf') }).Count -eq 0) 'no user data or OAuth secrets included'
    $controls = @(Read-Rows 'SELECT `Dialog_`, `Control`, `Type`, `Property`, `Attributes` FROM `Control`' 5)
    Assert (@($controls | Where-Object { $_[0] -eq 'ServiceAccountDlg' -and $_[3] -eq 'SERVICEPASSWORD' -and ([int]$_[4] -band 2097152) }).Count -eq 1) 'password field is masked'
    foreach ($dialog in @('ServerOptionsDlg','ServiceAccountDlg','ServerReadyDlg')) {
        Assert (@($controls | Where-Object { $_[0] -eq $dialog }).Count -gt 0) "wizard includes $dialog"
    }
    $components = @{}
    foreach ($row in (Read-Rows 'SELECT `Component`, `Attributes` FROM `Component`' 2)) { $components[$row[0]] = [int]$row[1] }
    Assert (($components['PersistentDataFolder'] -band 16) -ne 0) 'data directory survives uninstall'
    Assert (($components['SavedSettings'] -band 16) -ne 0) 'settings for later reinstall survive uninstall'
    $aclRows = @(Read-Rows 'SELECT `LockObject`, `SDDLText` FROM `MsiLockPermissionsEx`' 2)
    Assert ($aclRows.Count -eq 3) 'data, configuration and program folders receive restricted ACLs'
    $sddl = ($aclRows | Where-Object { $_[0] -eq 'DATADIR' })[1]
    Assert (!$sddl.Contains('[')) 'native MSI SDDL contains no unsupported property references'
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($sddl)
    $allowedSids = @($descriptor.DiscretionaryAcl | ForEach-Object { $_.SecurityIdentifier.Value })
    Assert ($allowedSids.Count -eq 3 -and $allowedSids -contains 'S-1-5-80-3680015686-339733092-3489414343-1427611537-2043285906' -and $allowedSids -contains 'S-1-5-18' -and $allowedSids -contains 'S-1-5-32-544' -and ($descriptor.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected)) 'valid base ACL grants only SYSTEM, administrators and the virtual service and blocks inherited access'
    $permissions = @(Read-Rows 'SELECT `SecureObject`, `Domain`, `User`, `Permission` FROM `Wix4SecureObject`' 4)
    Assert (@($permissions | Where-Object { $_[0] -eq 'DATADIR' -and $_[1] -eq '[SERVICEDOMAIN]' -and $_[2] -eq '[SERVICEUSER]' -and ([long]$_[3] -band 0x10000000) }).Count -eq 1) 'WiX grants the selected service account full access through formatted fields'
    Assert (@(Read-Rows 'SELECT `Name` FROM `Wix5FirewallException`').Count -eq 2) 'private and domain firewall rules included'
    $rules = @(Read-Rows 'SELECT `Profile`, `RemoteAddresses`, `Port`, `Program` FROM `Wix5FirewallException`' 4)
    Assert (@($rules | Where-Object { $_[0] -notin @('1','2') -or $_[1] -ne 'LocalSubnet' -or $_[2] -ne '[PORT]' -or $_[3] -ne '[#ServerExe]' }).Count -eq 0) 'firewall scope excludes public networks and other programs/ports'
    $events = @(Read-Rows 'SELECT `Dialog_`, `Event`, `Argument` FROM `ControlEvent`' 3)
    Assert (@($events | Where-Object { $_[0] -eq 'ServerReadyDlg' -and $_[1] -eq 'Reinstall' -and $_[2] -eq 'All' }).Count -eq 1) 'repair wizard requests actual file reinstallation'
    $sequence = @{}
    foreach ($row in (Read-Rows 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) { $sequence[$row[0]] = [int]$row[1] }
    Assert ($sequence['ValidateExecute'] -lt $sequence['InstallInitialize']) 'preflight runs before changes begin'
    Assert ($sequence['StopServices'] -gt $sequence['InstallInitialize'] -and $sequence['StopServices'] -lt $sequence['RemoveFiles'] -and $sequence['DeleteServices'] -lt $sequence['RemoveFiles']) 'service stops and is removed inside the transaction before its files are deleted'
    Assert ($sequence['RemoveExistingProducts'] -gt $sequence['InstallInitialize'] -and $sequence['RemoveExistingProducts'] -lt $sequence['InstallFiles']) 'upgrade removal is inside the rollback transaction'
    Assert ($sequence['ConfigureStartMode'] -gt $sequence['InstallServices'] -and $sequence['ConfigureStartMode'] -lt $sequence['StartServices']) 'startup mode is configured before the service starts'
    Assert ($sequence['WriteServiceConfig'] -gt $sequence['InstallServices'] -and $sequence['WriteServiceConfig'] -lt $sequence['StartServices']) 'configuration exists before the service starts'
    Assert (($components['PersistentConfigurationFolder'] -band 16) -ne 0) 'configuration survives uninstall'
    $secureAction = @($sequence.Keys | Where-Object { $_ -match 'SchedSecureObjects_' })
    Assert ($secureAction.Count -eq 1 -and $sequence[$secureAction[0]] -gt $sequence['CreateFolders'] -and $sequence[$secureAction[0]] -lt $sequence['StartServices']) 'service folder access is scheduled after folder creation and before service startup'
    Write-Host "Installer table checks passed: $($files.Count) packaged files. No installation was performed."
} finally {
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
