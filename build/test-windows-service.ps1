param([Parameter(Mandatory)][string]$Payload, [Parameter(Mandatory)][string]$Result)
$ErrorActionPreference = 'Stop'
$log = [Collections.Generic.List[string]]::new()
$exe = $null
function Assert($ok, $name) { if (!$ok) { throw $name }; $log.Add('PASS: ' + $name) }
function Run([string[]]$Arguments, [int]$Expected = 0) {
    $psi = [Diagnostics.ProcessStartInfo]::new($script:exe)
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $p = [Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
    if (!$p.WaitForExit(95000)) { $p.Kill(); throw 'Command timed out' }
    $output = $stdout.Result + $stderr.Result
    $log.Add($output)
    if ($p.ExitCode -ne $Expected) { throw "Exit $($p.ExitCode), expected $Expected : $($Arguments -join ' ')" }
    $p.Dispose()
    return $output
}
function Sddl([string]$path) { (Get-Acl -LiteralPath $path).Sddl }
try {
    if (Get-Service KommitEye -ErrorAction SilentlyContinue) { throw 'A real KommitEye service exists; refusing to replace it.' }
    $root = Join-Path $env:PUBLIC ('Documents\KommitEye SCM checks ' + [guid]::NewGuid().ToString('N'))
    $program = Join-Path $root 'Program With Spaces'
    New-Item -ItemType Directory -Path $program -Force | Out-Null
    Copy-Item -Path (Join-Path $Payload '*') -Destination $program -Recurse
    $exe = Join-Path $program 'KommitEye.Server.exe'
    $config = Join-Path $root 'configuration with spaces.json'
    $data = Join-Path $root 'Data With Spaces'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
    Run @('--initialize-config','--config',$config,'--data-dir',$data,'--urls',"http://127.0.0.1:$port",'--credential-protection','LocalMachine') | Out-Null
    $paths = @($root,$program,$exe,$config)
    $acls = @{}; foreach($path in $paths) { $acls[$path] = Sddl $path }
    $original = (Get-FileHash $config).Hash
    Run @('--uninstall-service') | Out-Null
    Run @('--install-service','--config',$config) | Out-Null
    $service=Get-CimInstance Win32_Service -Filter "Name='KommitEye'"
    Assert ($service.StartName -eq 'NT SERVICE\KommitEye' -and $service.StartMode -eq 'Auto' -and $service.State -eq 'Stopped') 'default virtual account, automatic startup, no web process on registration'
    Assert ($service.PathName -eq ('"'+$exe+'" --config "'+$config+'"')) 'SCM executable and config paths with spaces are quoted'
    Run @('--install-service','--config',$config) | Out-Null
    Run @('--install-service','--config',$config,'--service-account','LocalSystem') 1 | Out-Null
    Assert ((Get-CimInstance Win32_Service -Filter "Name='KommitEye'").StartName -eq 'NT SERVICE\KommitEye') 'existing account cannot change silently'
    Run @('--start-service') | Out-Null
    $response=Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/api/v1/setup"
    Assert ($response.StatusCode -eq 200) 'real Windows service responds over HTTP'
    $snapshot = Join-Path $root 'snapshot.json'
    Run @('--service-snapshot',$snapshot) | Out-Null
    Run @('--stop-service') | Out-Null
    Run @('--install-service','--config',$config,'--service-start','manual') | Out-Null
    Run @('--restore-service',$snapshot) | Out-Null
    Assert ((Get-CimInstance Win32_Service -Filter "Name='KommitEye'").StartMode -eq 'Auto' -and (Get-Service KommitEye).Status -eq 'Running') 'rollback restores running state and startup mode'
    Run @('--uninstall-service') | Out-Null
    Assert (!(Get-Service KommitEye -ErrorAction SilentlyContinue)) 'running service stopped and removed'
    Assert ((Get-FileHash $config).Hash -eq $original -and (Test-Path (Join-Path $data 'reports.db'))) 'configuration and reports database survive removal'
    foreach ($path in $paths) { Assert ((Sddl $path) -eq $acls[$path]) ('ACL unchanged: '+$path) }
    Run @('--uninstall-service') | Out-Null
    # An administrator-owned directory under the user profile is inaccessible to the virtual account.
    $denied = Join-Path $env:LOCALAPPDATA ('KommitEye SCM denied ' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $denied | Out-Null
    $deniedAcl = Sddl $denied
    $deniedConfig = Join-Path $root 'denied.json'
    Run @('--initialize-config','--config',$deniedConfig,'--data-dir',$denied,'--urls',"http://127.0.0.1:$port",'--credential-protection','LocalMachine') | Out-Null
    Run @('--install-service','--config',$deniedConfig) | Out-Null
    $failure=Run @('--start-service') 1
    Assert ($failure -match 'access|account') 'real access failure produces actionable service error'
    Assert ((Sddl $denied) -eq $deniedAcl) 'failed start does not repair ACL or inheritance'
    Run @('--uninstall-service') | Out-Null
    $log.Add('SUCCESS: real SCM checks complete. Fixture: '+$root)
} catch { $log.Add('FAIL: '+$_); throw }
finally {
    if ($exe -and (Get-Service KommitEye -ErrorAction SilentlyContinue)) { try { Run @('--uninstall-service') | Out-Null } catch { $log.Add('Cleanup failed: '+$_) } }
    $log | Set-Content -LiteralPath $Result -Encoding UTF8
}
