param([Parameter(Mandatory)][string]$Msi,[Parameter(Mandatory)][string]$Payload,[Parameter(Mandatory)][string]$Result)
$ErrorActionPreference='Stop'
Start-Transcript -Path ($Result+'.transcript') -Force | Out-Null
$notes=[Collections.Generic.List[string]]::new()
$registry='HKLM:\SOFTWARE\KommitEye\Server'
$originalRegistry=@{}
if(Test-Path $registry) { (Get-Item $registry).GetValueNames() | ForEach-Object { $originalRegistry[$_] = @((Get-Item $registry).GetValue($_),(Get-Item $registry).GetValueKind($_)) } }
$installed=$null
function Assert($ok,$name) { if(!$ok){throw $name}; $notes.Add('PASS: '+$name); $notes | Set-Content $Result }
function Install([string]$package,[string]$extra='', [int]$expected=0) {
 $log=Join-Path $root ('msi-'+[guid]::NewGuid().ToString('N')+'.log')
 $args='/i "'+$package+'" /qn /norestart /L*v "'+$log+'" INSTALLFOLDER="'+$program+'" CONFIGDIR="'+$configDir+'" DATADIR="'+$data+'" GITEXE="'+$git+'" PORT='+$port+' ACCESSMODE=local FIREWALL=0 AUTOSTART=1 STARTNOW=1 '+$extra
 $p=Start-Process msiexec.exe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
 $notes.Add('MSI log: '+$log+' exit='+$p.ExitCode)
 Assert ($p.ExitCode -eq $expected) ('MSI expected exit '+$expected)
}
function Remove([string]$package,[string]$extra='', [int]$expected=0) {
 $log=Join-Path $root ('remove-'+[guid]::NewGuid().ToString('N')+'.log')
 $p=Start-Process msiexec.exe -ArgumentList ('/x "'+$package+'" /qn /norestart /L*v "'+$log+'" '+$extra) -Wait -PassThru -WindowStyle Hidden
 $notes.Add('MSI removal log: '+$log+' exit='+$p.ExitCode)
 Assert ($p.ExitCode -eq $expected) ('MSI removal expected exit '+$expected)
}
function Fixture([string]$source,[string]$target,[bool]$upgrade) {
 Copy-Item -LiteralPath $source -Destination $target
 $wi=New-Object -ComObject WindowsInstaller.Installer
 $db=$wi.OpenDatabase($target,1)
 function Sql($query) { $v=$db.OpenView($query); try { $v.Execute() } catch { throw ($query+" : "+$_) }; $v.Close(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($v) }
 Sql "INSERT INTO ``CustomAction`` (``Action``,``Type``,``Target``) VALUES ('FixtureFailure',19,'Deliberate transaction rollback test')"
 $v=$db.OpenView("SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='InstallExecuteAgain'"); $v.Execute(); $r=$v.Fetch(); $seq=$r.IntegerData(1)+1; $v.Close()
 Sql "INSERT INTO ``InstallExecuteSequence`` (``Action``,``Condition``,``Sequence``) VALUES ('FixtureFailure','KOMMITEYE_TEST_FAIL=1',$seq)"
 if($upgrade) {
  $versionView=$db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"); $versionView.Execute(); $versionRecord=$versionView.Fetch()
  $oldVersion=$versionRecord.StringData(1); $parsed=[version]$oldVersion; $newVersion="$($parsed.Major).$($parsed.Minor).$($parsed.Build+1)"
  $versionView.Close(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($versionRecord); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($versionView)
  Sql "UPDATE ``Property`` SET ``Value``='$newVersion' WHERE ``Property``='ProductVersion'"
  $code='{'+[guid]::NewGuid().ToString().ToUpperInvariant()+'}'
  Sql "UPDATE ``Property`` SET ``Value``='$code' WHERE ``Property``='ProductCode'"
  $v=$db.OpenView('SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty` FROM `Upgrade`')
  $v.Execute(); $rows=@(); while($r=$v.Fetch()) { $rows+=,$r }; $v.Close()
  Sql 'DELETE FROM `Upgrade`'
  $v=$db.OpenView('INSERT INTO `Upgrade` (`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`) VALUES (?,?,?,?,?,?,?)')
  foreach($r in $rows) {
   foreach($column in @(2,3)){if($r.StringData($column) -eq $oldVersion){$r.StringData($column)=$newVersion}}
   $v.Execute($r)
  }
  $v.Close()
 }
 $summary=$db.SummaryInformation(1); $summary.Property(9)='{'+[guid]::NewGuid().ToString().ToUpperInvariant()+'}'; $summary.Persist()
 $db.Commit()
 [void][Runtime.InteropServices.Marshal]::ReleaseComObject($summary)
 if($v){[void][Runtime.InteropServices.Marshal]::ReleaseComObject($v)}
 if($r){[void][Runtime.InteropServices.Marshal]::ReleaseComObject($r)}
 [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($wi)
}
try {
 if(Get-Service KommitEye -ErrorAction SilentlyContinue){throw 'Existing real service; refusing test.'}
 $root=Join-Path $env:PUBLIC ('Documents\KommitEye MSI checks '+[guid]::NewGuid().ToString('N'))
 $program=Join-Path $root 'Program With Spaces'; $configDir=Join-Path $root 'Config With Spaces'; $data=Join-Path $root 'Data With Spaces'; $repository=Join-Path $root 'Existing Repository'
 foreach($path in @($program,$configDir,$data,$repository)){New-Item -ItemType Directory -Path $path -Force | Out-Null}
 $git=(Get-Command git.exe).Source
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
 $config=Join-Path $configDir 'server.json'
 & (Join-Path $Payload 'KommitEye.Server.exe') --initialize-config --config $config --data-dir $data --urls "http://127.0.0.1:$port" --git-path $git --credential-protection LocalMachine | Out-Null
 if($LASTEXITCODE){throw 'Initialize failed'}
 $report=Join-Path $data 'preserved-report.md'; 'Preserve this review' | Set-Content $report
 $repoFile=Join-Path $repository 'existing.txt'; 'Preserve repository' | Set-Content $repoFile
 $paths=@($root,$program,$configDir,$data,$repository,$config,$report,$repoFile)
 $acls=@{}; foreach($path in $paths){$acls[$path]=(Get-Acl $path).Sddl}
 $hash=(Get-FileHash $config).Hash
 $base=Join-Path $root 'base fixture.msi'; $upgrade=Join-Path $root 'upgrade fixture.msi'
 Fixture $Msi $base $false; Fixture $Msi $upgrade $true
 [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
 Install $base 'KOMMITEYE_TEST_FAIL=1' 1603
 Assert (!(Get-Service KommitEye -ErrorAction SilentlyContinue)) 'failed install leaves no service'
 foreach($path in $paths){Assert ((Get-Acl $path).Sddl -eq $acls[$path]) ('failed install preserves ACL '+$path)}
 Install $base; $installed=$base
 Assert ((Get-Service KommitEye).Status -eq 'Running') 'MSI starts real service'
 Install $base 'REINSTALL=ALL REINSTALLMODE=amus'
 Assert ((Get-Service KommitEye).Status -eq 'Running') 'repair starts service'
 Install $upgrade 'KOMMITEYE_TEST_FAIL=1' 1603
 Assert ((Get-Service KommitEye).Status -eq 'Running') 'failed upgrade restores running service'
 Assert ((Get-CimInstance Win32_Service -Filter "Name='KommitEye'").StartName -eq 'NT SERVICE\KommitEye') 'failed upgrade preserves account'
 Install $upgrade; $installed=$upgrade
 Assert ((Get-Service KommitEye).Status -eq 'Running') 'upgrade starts real service'
 Remove $upgrade 'KOMMITEYE_TEST_FAIL=1' 1603
 Assert ((Get-Service KommitEye).Status -eq 'Running') 'failed uninstall restores running service'
 Remove $upgrade; $installed=$null
 Assert (!(Get-Service KommitEye -ErrorAction SilentlyContinue)) 'uninstall removes service'
 Assert ((Get-FileHash $config).Hash -eq $hash -and (Get-Content $report) -eq 'Preserve this review' -and (Get-Content $repoFile) -eq 'Preserve repository' -and (Test-Path (Join-Path $data 'reports.db'))) 'configuration reports database and repository survive all operations'
 foreach($path in $paths){Assert ((Get-Acl $path).Sddl -eq $acls[$path]) ('ACL/inheritance unchanged '+$path)}
 $notes.Add('SUCCESS: actual MSI install/repair/upgrade/rollback/uninstall. Fixture: '+$root)
} catch { $notes.Add('FAIL: '+$_); throw }
finally {
 $notes | Set-Content $Result
 if($installed){try{Remove $installed}catch{$notes.Add('Cleanup failed: '+$_)}}
 if(Test-Path $registry){
  $key=Get-Item $registry
  foreach($name in $key.GetValueNames()){if(!$originalRegistry.ContainsKey($name)){Remove-ItemProperty -LiteralPath $registry -Name $name}}
  foreach($name in $originalRegistry.Keys){Set-ItemProperty -LiteralPath $registry -Name $name -Value $originalRegistry[$name][0] -Type $originalRegistry[$name][1]}
 }
 $notes | Set-Content $Result
 Stop-Transcript | Out-Null
}
