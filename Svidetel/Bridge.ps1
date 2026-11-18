param()
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$script:items = New-Object 'System.Collections.Generic.List[object]'
$script:errors = New-Object 'System.Collections.Generic.List[string]'
$script:runKeys = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce')
function Add-Item($kind,$name,$command,$location,$state,$detail,$can=$false) {
 $script:items.Add([ordered]@{Id="$kind|$location|$name";Kind=$kind;Name=[string]$name;Command=[string]$command;Location=[string]$location;State=[string]$state;Detail=[string]$detail;CanDisable=[bool]$can})
}
function Section($name,[scriptblock]$block) { try { & $block } catch { $script:errors.Add("${name}: $($_.Exception.Message)") } }
function Exe-Path([string]$command) {
 $c=[Environment]::ExpandEnvironmentVariables($command.Trim())
 if($c -match '^"([^"]+)"') { return $matches[1] }
 if($c -match '^(.*?\.(?:exe|com|sys|bat|cmd|ps1|vbs|js))(?=\s|$)') {return $matches[1]}
 return $c
}
function Protect-Command([string]$command) {
 $p=Exe-Path $command
 if(-not [IO.Path]::IsPathRooted($p) -or -not (Test-Path -LiteralPath $p -PathType Leaf)) {throw 'Cannot verify executable. Modification blocked.'}
 $full=[IO.Path]::GetFullPath($p)
 if($full.StartsWith($env:windir+'\',[StringComparison]::OrdinalIgnoreCase) -or $full -match '(?i)Windows Defender|Microsoft Defender|SecurityHealth|MsMpEng') {throw 'Protected Windows/security component.'}
 $sig=Get-AuthenticodeSignature -LiteralPath $full
 if($sig.SignerCertificate -and $sig.SignerCertificate.Subject -match '(?i)Microsoft') {throw 'Microsoft component protected from modification.'}
 return (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
}
function Startup-Roots { @([Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup')) }
function Validate-Startup([string]$path) {
 $full=[IO.Path]::GetFullPath($path)
 if((Startup-Roots) -notcontains [IO.Path]::GetDirectoryName($full)) {throw 'Not a direct startup-folder entry.'}
 if([IO.Path]::GetFileName($full) -eq 'desktop.ini') {throw 'Protected metadata.'}
 if((Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($full))).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Reparse startup directory blocked.'}
 return $full
}
function Snapshot($item) {
 switch($item.Kind) {
  'run' {
   if($script:runKeys -notcontains $item.Location) {throw 'Unsupported registry key.'}
   $key=Get-Item -LiteralPath $item.Location
   if($key.GetValueNames() -notcontains $item.Name) {return [ordered]@{Exists=$false}}
   $value=$key.GetValue($item.Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
   return [ordered]@{Exists=$true;Value=$value;ValueKind=[string]$key.GetValueKind($item.Name)}
  }
  'task' {
   if($item.Location -like '\Microsoft\*' -or $item.Name -match '[\\/]') {throw 'Protected/invalid task.'}
   $t=Get-ScheduledTask -TaskPath $item.Location -TaskName $item.Name
   if($t.Author -match '(?i)Microsoft') {throw 'Microsoft task protected.'}
   $xml=Export-ScheduledTask -TaskPath $item.Location -TaskName $item.Name
   # Ignore only task Settings/Enabled, not trigger-specific Enabled elements.
   [xml]$doc=$xml; $ns=New-Object Xml.XmlNamespaceManager($doc.NameTable); $ns.AddNamespace('t',$doc.DocumentElement.NamespaceURI)
   $node=$doc.SelectSingleNode('/t:Task/t:Settings/t:Enabled',$ns); if($node){$null=$node.ParentNode.RemoveChild($node)}
   $hash=[Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($doc.OuterXml)))
   return [ordered]@{Exists=$true;Enabled=[bool]$t.Settings.Enabled;Identity=$hash;Commands=@($t.Actions | ForEach-Object {[string]$_.Execute})}
  }
  'service' {
   if($item.Name -notmatch '^[\w .\-]+$') {throw 'Unsupported service name.'}
   $s=Get-CimInstance Win32_Service | Where-Object Name -EQ $item.Name
   if(-not $s) {throw 'Service missing.'}
   $key=Get-Item -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Services\"+$s.Name)
   return [ordered]@{Exists=$true;Start=[int]$key.GetValue('Start');Delayed=$key.GetValue('DelayedAutoStart',$null);Path=[string]$s.PathName;Account=[string]$s.StartName}
  }
  'startup' {
   $path=Validate-Startup $item.Location
   if(-not (Test-Path -LiteralPath $path)) {return [ordered]@{Exists=$false}}
   $f=Get-Item -LiteralPath $path
   if($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Directories/reparse points blocked.'}
   if($f.Length -gt 1MB) {throw 'Startup entry too large for reversible storage.'}
   return [ordered]@{Exists=$true;Bytes=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path));Hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
  }
  default {throw 'Read-only source.'}
 }
}
function Same($a,$b) { (ConvertTo-Json -InputObject $a -Depth 12 -Compress) -ceq (ConvertTo-Json -InputObject $b -Depth 12 -Compress) }
function Assert-Identity($item,$original,$current) {
 if($item.Kind -eq 'task' -and $current.Identity -ne $original.Identity) {throw 'Task definition changed. Rescan required.'}
 if($item.Kind -eq 'service' -and ($current.Path -cne $original.Path -or $current.Account -cne $original.Account)) {throw 'Service definition changed.'}
}
function Mutate($item,$before,[bool]$restore) {
 $current=Snapshot $item
 if(-not $before.Exists) {throw 'Original entry did not exist.'}
 Assert-Identity $item $before $current
 if($restore) {
  if(Same $current $before) {return $current}
  switch($item.Kind) {
   'run' {if($current.Exists) {throw 'Registry entry recreated. Refusing overwrite.'}}
   'startup' {if($current.Exists) {throw 'Startup entry recreated. Refusing overwrite.'}}
   'task' {if($current.Enabled) {throw 'Unexpected task state.'}}
   'service' {if($current.Start -ne 4 -or $current.Delayed -ne $before.Delayed) {throw 'Service configuration changed.'}}
  }
 } elseif(-not (Same $current $before)) {throw 'Entry changed since inspection. Rescan required.'}
 switch($item.Kind) {
  'run' {
   if($before.ValueKind -notin @('String','ExpandString')) {throw 'Unsupported registry value type.'}
   $null=Protect-Command ([string]$before.Value)
   $hive=if($item.Location.StartsWith('HKCU:')){[Microsoft.Win32.Registry]::CurrentUser}else{[Microsoft.Win32.Registry]::LocalMachine}
   $key=$hive.OpenSubKey($item.Location.Substring(6),$true)
   if(-not $key){throw 'Registry key missing or not writable.'}
   try {if($restore) {$key.SetValue($item.Name,[string]$before.Value,[Microsoft.Win32.RegistryValueKind]$before.ValueKind)} else {$key.DeleteValue($item.Name,$true)}} finally {$key.Dispose()}
  }
  'task' {
   if(-not $before.Enabled) {throw 'Task was already disabled.'}
   if($before.Commands.Count -eq 0) {throw 'Task action cannot be verified.'}
   foreach($c in $before.Commands) {$null=Protect-Command $c}
   if($restore) {$null=Enable-ScheduledTask -TaskPath $item.Location -TaskName $item.Name} else {$null=Disable-ScheduledTask -TaskPath $item.Location -TaskName $item.Name}
  }
  'service' {
   $null=Protect-Command $before.Path
   if($before.Start -notin @(2,3)) {throw 'Only Automatic/Manual third-party services supported.'}
   $mode=if(-not $restore){'Disabled'}elseif($before.Start -eq 2){'Automatic'}else{'Manual'}
   Set-Service -Name $item.Name -StartupType $mode
   # Do not stop running services or change DelayedAutoStart.
  }
  'startup' {
   $path=Validate-Startup $item.Location
   if($restore) {
    $bytes=[Convert]::FromBase64String($before.Bytes)
    $hash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-','')
    if($hash -ne $before.Hash) {throw 'Backup checksum mismatch.'}
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew); try {$stream.Write($bytes,0,$bytes.Length)} finally {$stream.Dispose()}
   } else {
    # Only shortcuts supported for mutations; raw executables remain read-only.
    if([IO.Path]::GetExtension($path) -ne '.lnk') {throw 'Only startup shortcuts may be disabled.'}
    $shell=New-Object -ComObject WScript.Shell; $target=$shell.CreateShortcut($path).TargetPath
    $null=Protect-Command ('"'+$target+'"')
    [IO.File]::Delete($path)
   }
  }
 }
 $after=Snapshot $item
 if($restore) {if(-not (Same $after $before)) {throw 'Restore verification failed; inspect history.'}}
 else {
  $ok=switch($item.Kind){'run'{-not $after.Exists};'startup'{-not $after.Exists};'task'{-not $after.Enabled};'service'{$after.Start -eq 4}}
  if(-not $ok){throw 'Disable verification failed; inspect history.'}
 }
 return $after
}
try {
 switch($request.op) {
  'scan' {
   Section 'Registry' {foreach($path in $script:runKeys) {if(Test-Path -LiteralPath $path){$key=Get-Item -LiteralPath $path; foreach($n in $key.GetValueNames()){Add-Item 'run' $n ([string]$key.GetValue($n)) $path 'Present' 'Login startup entry. Disabling does not stop an existing process.' $true}}}}
   Section 'Scheduled tasks' {foreach($t in Get-ScheduledTask){$commands=($t.Actions | ForEach-Object {"$($_.Execute) $($_.Arguments)"}) -join '; '; Add-Item 'task' $t.TaskName $commands $t.TaskPath ([string]$t.State) ("Author: "+$t.Author+"; triggers: "+(($t.Triggers.CimClass.CimClassName) -join ', ')) ($t.TaskPath -notlike '\Microsoft\*' -and $t.Author -notmatch '(?i)Microsoft' -and $t.Settings.Enabled)}}
   Section 'Services' {foreach($s in Get-CimInstance Win32_Service){Add-Item 'service' $s.Name $s.PathName ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$s.Name) ("$($s.State) / $($s.StartMode)") ("Account: "+$s.StartName+"; "+$s.DisplayName+". Disabling affects future starts, not current execution.") ($s.StartMode -ne 'Disabled' -and $s.PathName -notmatch '(?i)\\Windows\\|Defender|MsMpEng')}}
   Section 'Drivers' {foreach($s in Get-CimInstance Win32_SystemDriver){Add-Item 'driver' $s.Name $s.PathName 'Win32_SystemDriver' ("$($s.State) / $($s.StartMode)") 'Kernel driver. Read-only.'}}
   Section 'Startup folders' {$shell=New-Object -ComObject WScript.Shell; foreach($root in Startup-Roots){if(Test-Path -LiteralPath $root){foreach($f in Get-ChildItem -LiteralPath $root -File -Force){$target=$f.FullName;if($f.Extension -eq '.lnk'){$l=$shell.CreateShortcut($f.FullName);$target='"'+$l.TargetPath+'" '+$l.Arguments};Add-Item 'startup' $f.Name $target $f.FullName 'Present' 'Startup folder entry.' ($f.Extension -eq '.lnk')}}}}
   Section 'Processes' {$procs=@(Get-CimInstance Win32_Process);$map=@{};foreach($p in $procs){$map[[int]$p.ProcessId]=$p.Name};foreach($p in $procs){Add-Item 'process' $p.Name $p.CommandLine ("PID "+$p.ProcessId) 'Running' ("Executable: "+$p.ExecutablePath+"; parent PID "+$p.ParentProcessId+" ("+$map[[int]$p.ParentProcessId]+"). Parent PID may have been reused; this is not proof of origin.")}}
   Section 'WMI subscriptions' {foreach($b in Get-CimInstance -Namespace root/subscription -ClassName __FilterToConsumerBinding){Add-Item 'wmi' ([string]$b.Consumer) ([string]$b.Filter) 'root/subscription' 'Persistent binding' 'Permanent WMI subscription; read-only. May be legitimate.'};foreach($c in Get-CimInstance -Namespace root/subscription -ClassName __EventConsumer){Add-Item 'wmi' $c.Name ([string]$c.CommandLineTemplate+[string]$c.ScriptText) 'root/subscription/consumer' 'Consumer' 'Persistent event consumer; read-only.'}}
   Section 'Installed software' {foreach($root in @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')){if(Test-Path $root){foreach($k in Get-ChildItem $root){$p=Get-ItemProperty $k.PSPath;if($p.DisplayName){Add-Item 'software' $p.DisplayName $p.UninstallString $k.Name ([string]$p.DisplayVersion) ("Publisher: "+$p.Publisher+"; install: "+$p.InstallLocation+". Uninstall through Windows Settings.")}}}}}
   Section 'TCP connections' {foreach($c in Get-NetTCPConnection -State Established,Listen){Add-Item 'network' ("PID "+$c.OwningProcess) ("$($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort)") ("TCP/"+$c.OwningProcess+"/"+$c.LocalAddress+"/"+$c.LocalPort+"/"+$c.RemoteAddress+"/"+$c.RemotePort) ([string]$c.State) 'Connection is not evidence of malware.'}}
   Section 'Browser extensions' {foreach($browser in @(@('Chrome',"$env:LOCALAPPDATA\Google\Chrome\User Data"),@('Edge',"$env:LOCALAPPDATA\Microsoft\Edge\User Data"))){if(Test-Path -LiteralPath $browser[1]){foreach($profile in Get-ChildItem -LiteralPath $browser[1] -Directory){$ext=Join-Path $profile.FullName 'Extensions';if(Test-Path -LiteralPath $ext){foreach($id in Get-ChildItem -LiteralPath $ext -Directory){foreach($version in Get-ChildItem -LiteralPath $id.FullName -Directory){$manifest=Join-Path $version.FullName 'manifest.json';if(Test-Path -LiteralPath $manifest){try{$m=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8|ConvertFrom-Json;Add-Item 'extension' ([string]$m.name) (($m.permissions+$m.host_permissions) -join ', ') $manifest ([string]$m.version) ($browser[0]+' / '+$profile.Name+' / '+$id.Name+'. Folder presence does not prove extension is enabled.')}catch{$script:errors.Add('Extension manifest: '+$_.Exception.Message)}}}}}}}}}
   [ordered]@{Items=@($script:items.ToArray());Errors=@($script:errors.ToArray())}|ConvertTo-Json -Depth 12 -Compress
  }
  'snapshot' {Snapshot $request.item | ConvertTo-Json -Depth 12 -Compress}
  'monitor' {
   $returned=@();$failures=@()
   foreach($entry in $request.entries){try{$s=Snapshot $entry.Item;$back=switch($entry.Item.Kind){'run'{$s.Exists};'startup'{$s.Exists};'task'{$s.Enabled};'service'{$s.Start -ne 4};default{$false}};if($back){$returned+=([string]$entry.Item.Name)}}catch{$failures+=([string]$entry.Item.Name+': '+$_.Exception.Message)}}
   [ordered]@{Returned=@($returned);Errors=@($failures)}|ConvertTo-Json -Depth 5 -Compress
  }
  'disable' {Mutate $request.item $request.expected $false | ConvertTo-Json -Depth 12 -Compress}
  'restore' {Mutate $request.item $request.expected $true | ConvertTo-Json -Depth 12 -Compress}
  'inspect' {
   $path=Exe-Path $request.command
   if(-not [IO.Path]::IsPathRooted($path) -or -not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'Cannot resolve an existing absolute executable path.'}
   $f=Get-Item -LiteralPath $path; $s=Get-AuthenticodeSignature -LiteralPath $path
   [ordered]@{Path=$f.FullName;Bytes=$f.Length;Modified=$f.LastWriteTimeUtc.ToString('o');SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;Signature=[string]$s.Status;Signer=[string]$s.SignerCertificate.Subject;Note='Signature is identity evidence, not a safety verdict. No file is uploaded.'}|ConvertTo-Json -Compress
  }
  default {throw 'Unsupported operation.'}
 }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
