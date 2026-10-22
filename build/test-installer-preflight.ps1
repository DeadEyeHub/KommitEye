param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
# Open an MSI session and invoke only prerequisite searches and immediate, read-only preflight actions.
# Never run INSTALL, ExecuteAction, InstallInitialize, or deferred setup actions here.
$installer = New-Object -ComObject WindowsInstaller.Installer
$installer.UILevel = 2
$session = $null
function Get-Property([string]$Name) {
    $session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $session, @($Name))
}
function Set-Property([string]$Name, [string]$Value) {
    [void]$session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @($Name, $Value))
}
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; Write-Host "PASS: $Message" }
try {
    $session = $installer.OpenPackage((Resolve-Path -LiteralPath $Path).Path, 0)
    Assert ($session.DoAction('AppSearch') -eq 1) 'prerequisite registry search succeeds'
    $database = $installer.OpenDatabase((Resolve-Path -LiteralPath $Path).Path, 0)
    $view = $database.OpenView('SELECT `Condition` FROM `InstallUISequence` WHERE `Action` = ''SetWIX_IS_NETFRAMEWORK_472_OR_LATER_INSTALLED''')
    [void]$view.Execute()
    $record = $view.Fetch()
    $frameworkCondition = $record.StringData(1)
    [void]$view.Close()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database)
    $detectedRelease = Get-Property 'WIXNETFX4RELEASEINSTALLED'
    foreach ($case in @(@('', 0), @('#461308', 0), @('#461808', 1), @('#533509', 1))) {
        Set-Property 'WIXNETFX4RELEASEINSTALLED' $case[0]
        Assert ($session.EvaluateCondition($frameworkCondition) -eq $case[1]) "Framework prerequisite correctly evaluates release '$($case[0])'"
    }
    Set-Property 'WIXNETFX4RELEASEINSTALLED' $detectedRelease
    Assert ($session.EvaluateCondition($frameworkCondition) -eq 1) 'installed Framework satisfies the actual MSI detection condition'
    Assert ($session.DoAction('SetWIX_IS_NETFRAMEWORK_472_OR_LATER_INSTALLED') -eq 1) 'WiX sets the Framework prerequisite property'
    Assert ($session.DoAction('LaunchConditions') -eq 1) 'actual MSI launch conditions pass on this computer'
    Assert ($session.DoAction('InitializeSettings') -eq 1) 'native DTF custom-action DLL loads and initializes'
    Set-Property 'FIREWALLCHOICE' ''
    Set-Property 'AUTOCHOICE' ''
    Set-Property 'STARTCHOICE' ''
    Assert ($session.DoAction('InitializeSettings') -eq 1) 'repeated initialization succeeds'
    Assert ((Get-Property 'FIREWALLCHOICE') -eq '' -and (Get-Property 'AUTOCHOICE') -eq '') 'unchecked options are not reset by repeated initialization'
    Set-Property 'PORT' '0'
    Assert ($session.DoAction('ValidateDialog') -eq 1 -and (Get-Property 'SETUPVALID') -eq '') 'invalid port keeps the wizard on the settings page'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    Set-Property 'PORT' ([string]$port)
    Set-Property 'NETWORKCHOICE' 'local'
    # Preserve persisted identity after a real uninstall: migration safeguards
    # intentionally reject a different data folder or service account.
    $saved = Get-ItemProperty 'HKLM:\SOFTWARE\KommitEye\Server' -ErrorAction SilentlyContinue
    $dataPath = if ($saved.DATADIR) { $saved.DATADIR } else { Join-Path ([IO.Path]::GetTempPath()) ('kommiteye-preflight-' + [guid]::NewGuid().ToString('N')) }
    $account = Get-Property 'SERVICEACCOUNT'
    $installedService = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\KommitEye' -ErrorAction SilentlyContinue
    if (!$installedService) {
        Assert ($account -eq 'NT SERVICE\KommitEye' -and (Get-Property 'LEGACYSERVICE') -ne '1') 'new installation ignores stale account registry values and skips password screen'
    }
    $dataExisted = Test-Path -LiteralPath $dataPath
    Set-Property 'DATADIR' $dataPath
    Set-Property 'SERVICEACCOUNT' $account
    # A fixture string is never used to log on or install a service by these immediate actions.
    Set-Property 'SERVICEPASSWORD' $(if ($account -eq 'NT SERVICE\KommitEye') { '' } else { 'installer-preflight-fixture' })
    Set-Property 'GITEXE' (Get-Command git.exe).Source
    Assert ($session.DoAction('ValidateDialog') -eq 1 -and (Get-Property 'SETUPVALID') -eq '1') 'valid settings pass the actual MSI action'
    Assert ((Get-Property 'CONFIGFILE').EndsWith('\server.json')) 'service receives an absolute config path'
    if ($account -eq 'NT SERVICE\KommitEye') { Assert ((Get-Property 'SERVICEPASSWORD') -eq '') 'virtual service validates without a password' }
    Assert ((Get-Property 'LISTENHOST') -eq '127.0.0.1') 'local access maps to loopback'
    Assert (!(Get-Property 'DATAPATH').EndsWith('\')) 'quoted service data argument has no trailing backslash'
    Assert ((Get-Property 'ConfigureStartMode') -eq 'mode=3') 'unchecked auto-start prepares demand startup'
    Assert ((Get-Property 'STARTNOW') -eq '0' -and (Get-Property 'FIREWALL') -eq '0') 'unchecked options become explicit zero values for elevation'
    Assert ((Test-Path -LiteralPath (Get-Property 'DATAPATH')) -eq $dataExisted) 'preflight does not create or remove the data directory'
    Set-Property 'AUTOCHOICE' '1'
    Assert ($session.DoAction('ValidateDialog') -eq 1 -and (Get-Property 'SETUPVALID') -eq '1' -and (Get-Property 'ConfigureStartMode') -eq 'mode=2') 'checked auto-start prepares automatic Windows startup'
    Write-Host 'Immediate preflight checks passed. No service, firewall, account or installation changes were made.'
} finally {
    if ($null -ne $session) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($session) }
    $installer.UILevel = 5
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
