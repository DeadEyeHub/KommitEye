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
    $tables = @(Read-Rows 'SELECT `Name` FROM `_Tables`') | ForEach-Object { $_[0] }
    Assert ($tables -notcontains 'ServiceInstall' -and $tables -notcontains 'ServiceControl') 'service registration and lifecycle delegated to executable'
    foreach ($table in @('MsiLockPermissionsEx','LockPermissions','Wix4SecureObject','Wix4User')) {
        Assert ($tables -notcontains $table) "no permission/account manipulation table: $table"
    }
    Assert ($tables -notcontains 'CreateFolder') 'existing data and configuration folders are not touched by MSI CreateFolder'
    $files = @(Read-Rows 'SELECT `FileName` FROM `File`') | ForEach-Object { $_[0].Split('|')[-1] }
    Assert ($files -contains 'KommitEye.Server.exe' -and $files -contains 'coreclr.dll') 'server and self-contained .NET runtime included'
    Assert ($files -contains 'index.html' -and @($files | Where-Object { $_ -like '*.js' }).Count -gt 0) 'compiled React UI included'
    Assert (@($files | Where-Object { $_ -like 'client_secret*' -or $_ -in @('admin.json','server.db','models.conf','settings.conf') }).Count -eq 0) 'no user data or OAuth secrets included'
    $controls = @(Read-Rows 'SELECT `Dialog_`, `Control`, `Type`, `Property`, `Attributes` FROM `Control`' 5)
    Assert (@($controls | Where-Object { $_[0] -eq 'ServiceAccountDlg' -or $_[3] -eq 'SERVICEPASSWORD' }).Count -eq 0) 'no legacy account or password dialog'
    foreach ($dialog in @('ServerOptionsDlg','NetworkOptionsDlg','ServiceOptionsDlg','ServerReadyDlg')) {
        Assert (@($controls | Where-Object { $_[0] -eq $dialog }).Count -gt 0) "wizard includes $dialog"
    }
    Assert (@($controls | Where-Object { $_[0] -eq 'ServiceOptionsDlg' -and $_[3] -eq 'AUTOCHOICE' }).Count -eq 1) 'automatic Windows startup is available'
    $routes = @(Read-Rows 'SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition` FROM `ControlEvent`' 5)
    Assert (@($routes | Where-Object { $_[0] -eq 'ServiceOptionsDlg' -and $_[2] -eq 'NewDialog' -and $_[3] -eq 'ServerReadyDlg' -and $_[4] -eq 'SETUPVALID = "1"' }).Count -eq 1) 'new installation validates settings and skips password dialog'
    Assert (@($controls | Where-Object { $_[0] -eq 'NetworkOptionsDlg' -and $_[3] -eq 'BINDIP' -and $_[2] -eq 'ComboBox' }).Count -eq 1) 'separate network page contains an IP selector'
    $networkText = @(Read-Rows 'SELECT `Text` FROM `Control` WHERE `Dialog_` = ''ServerReadyDlg'' AND `Control` = ''Network''')
    Assert ($networkText[0][0] -eq '[LANADDRESS]') 'ready page displays the resolved IP URL instead of computer name'
    $components = @{}
    foreach ($row in (Read-Rows 'SELECT `Component`, `Attributes` FROM `Component`' 2)) { $components[$row[0]] = [int]$row[1] }
    Assert (($components['PersistentDataFolder'] -band 16) -ne 0) 'data directory survives uninstall'
    Assert (($components['SavedSettings'] -band 16) -ne 0) 'settings for later reinstall survive uninstall'
    Assert (@(Read-Rows 'SELECT `Name` FROM `Wix5FirewallException`').Count -eq 2) 'private and domain firewall rules included'
    $rules = @(Read-Rows 'SELECT `Profile`, `RemoteAddresses`, `Port`, `Program` FROM `Wix5FirewallException`' 4)
    Assert (@($rules | Where-Object { $_[0] -notin @('1','2') -or $_[1] -ne 'LocalSubnet' -or $_[2] -ne '[PORT]' -or $_[3] -ne '[#ServerExe]' }).Count -eq 0) 'firewall scope excludes public networks and other programs/ports'
    $events = @(Read-Rows 'SELECT `Dialog_`, `Event`, `Argument` FROM `ControlEvent`' 3)
    Assert (@($events | Where-Object { $_[0] -eq 'ServerReadyDlg' -and $_[1] -eq 'Reinstall' -and $_[2] -eq 'All' }).Count -eq 1) 'repair wizard requests actual file reinstallation'
    $sequence = @{}
    foreach ($row in (Read-Rows 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) { $sequence[$row[0]] = [int]$row[1] }
    Assert ($sequence['ValidateExecute'] -lt $sequence['InstallInitialize']) 'preflight runs before changes begin'
    Assert ($sequence['BeginServiceTransaction'] -gt $sequence['InstallInitialize'] -and $sequence['BeginServiceTransaction'] -lt $sequence['RemoveExistingProducts']) 'snapshot and stop precede old-product removal inside transaction'
    Assert ($sequence['RollbackServiceTransaction'] -lt $sequence['BeginServiceTransaction']) 'original service restored after file rollback'
    Assert ($sequence['WriteServiceConfig'] -gt $sequence['InstallFiles'] -and $sequence['RegisterServer'] -gt $sequence['WriteServiceConfig']) 'executable initializes configuration before registration'
    Assert ($sequence['RollbackNewService'] -gt $sequence['InstallFiles'] -and $sequence['RollbackNewService'] -lt $sequence['RegisterServer'] -and $sequence['RegisterServer'] -gt $sequence['RemoveExistingProducts'] -and $sequence['InstallExecuteAgain'] -gt $sequence['RegisterServer']) 'failed registration/start is undone before file rollback'
    Assert (($components['PersistentConfigurationFolder'] -band 16) -ne 0) 'configuration survives uninstall'
    $actions = @(Read-Rows 'SELECT `Action`, `Type` FROM `CustomAction`' 2)
    Assert (@($actions | Where-Object { $_[0] -eq 'CommitServiceTransaction' -and ([int]$_[1] -band 512) }).Count -eq 1) 'uninstall deletes service only on commit, preserving credentials on rollback'
    Write-Host "Installer table checks passed: $($files.Count) packaged files. No installation was performed."
} finally {
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
