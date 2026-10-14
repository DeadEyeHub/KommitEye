param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$PayloadDirectory
)
$ErrorActionPreference = 'Stop'
# Run only costing and file-use detection against an existing published payload.
# This does not install, uninstall, stop services, or execute any custom actions.
$installer = New-Object -ComObject WindowsInstaller.Installer
$installer.UILevel = 2
$session = $null
try {
    $session = $installer.OpenPackage((Resolve-Path -LiteralPath $Path).Path, 0)
    $target = (Resolve-Path -LiteralPath $PayloadDirectory).Path.TrimEnd('\') + '\'
    [void]$session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @('INSTALLFOLDER', $target))
    foreach ($action in @('CostInitialize', 'FileCost', 'CostFinalize', 'InstallValidate')) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Write-Host "Starting $action"
        $result = $session.DoAction($action)
        $watch.Stop()
        Write-Host ('{0}: result={1}, seconds={2:N3}' -f $action, $result, $watch.Elapsed.TotalSeconds)
        if ($result -ne 1) { throw "$action failed with status $result" }
    }
} finally {
    if ($null -ne $session) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($session) }
    $installer.UILevel = 5
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
