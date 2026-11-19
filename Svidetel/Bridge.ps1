param()
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$script:items = New-Object 'System.Collections.Generic.List[object]'
$script:errors = New-Object 'System.Collections.Generic.List[string]'
$script:runKeys = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce')
function Add-Item($kind,$name,$command,$location,$state,$detail,$can=$false) {
 $target=''
 $missing=$false
 if($kind -in @('run','service','startup','task') -and $command -and $command -notmatch ';') {
  try {
   $target=Exe-Path $command
   $missing=Test-MissingLocalExe $target
  } catch { $target=''; $missing=$false }
 }
 $script:items.Add([ordered]@{Id="$kind|$location|$name";Kind=$kind;Name=[string]$name;Command=[string]$command;Location=[string]$location;State=[string]$state;Detail=[string]$detail;CanDisable=[bool]$can;Executable=$target;MissingTarget=$missing})
}
function Section($name,[scriptblock]$block) { try { & $block } catch { $script:errors.Add("${name}: $($_.Exception.Message)") } }
function Read-Events($log, $id, $hours, $limit, $provider = '') {
 $filter = @{LogName=$log; Id=$id; StartTime=(Get-Date).AddHours(-$hours)}
 if ($provider) { $filter.ProviderName = $provider }
 try { Get-WinEvent -FilterHashtable $filter -MaxEvents $limit }
 catch { if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw } }
}
function Read-EventData($event) {
 $data = @{}
 foreach ($field in ([xml]$event.ToXml()).Event.EventData.Data) {
  if ($field.Name) { $data[[string]$field.Name] = [string]$field.'#text' }
 }
 return $data
}
function Read-Launches {
 $result = [ordered]@{
  Processes=[Collections.Generic.List[object]]::new()
  Tasks=[Collections.Generic.List[object]]::new()
  Errors=[Collections.Generic.List[string]]::new()
 }
 $channels = @(
  @{ Log='Security'; Id=4688; Pid='NewProcessId'; Image='NewProcessName'; Parent='ParentProcessName'; Source='Security 4688' },
  @{ Log='Microsoft-Windows-Sysmon/Operational'; Id=1; Pid='ProcessId'; Image='Image'; Parent='ParentImage'; Source='Sysmon 1' }
 )
 foreach ($channel in $channels) {
  try {
   if ($channel.Id -eq 1 -and -not (Get-WinEvent -ListLog $channel.Log -ErrorAction SilentlyContinue)) { continue }
   foreach ($event in Read-Events $channel.Log $channel.Id 24 300) {
    $data = Read-EventData $event
    $idText = [string]$data[$channel.Pid]
    $processId = 0
    if ($idText -match '^0x[0-9a-f]{1,8}$') { $processId = [Convert]::ToInt32($idText.Substring(2),16) }
    else { $null = [int]::TryParse($idText,[ref]$processId) }
    if ($processId -le 0 -or -not $data[$channel.Image]) { continue }
    $result.Processes.Add([ordered]@{
     RecordId=$event.RecordId; Time=$event.TimeCreated.ToUniversalTime().ToString('o'); ProcessId=$processId
     Executable=$data[$channel.Image]; ParentExecutable=[string]$data[$channel.Parent]; Source=$channel.Source
    })
   }
  } catch { $result.Errors.Add($channel.Source + ': ' + $_.Exception.Message) }
 }
 $taskLog = 'Microsoft-Windows-TaskScheduler/Operational'
 try {
  if (-not (Get-WinEvent -ListLog $taskLog).IsEnabled) { throw 'Task Scheduler history is disabled.' }
  foreach ($event in Read-Events $taskLog 129 24 300) {
   $data = Read-EventData $event
   $processId = 0
   if ($data.TaskName -and [int]::TryParse([string]$data.ProcessId,[ref]$processId) -and $processId -gt 0) {
    $result.Tasks.Add([ordered]@{RecordId=$event.RecordId; Time=$event.TimeCreated.ToUniversalTime().ToString('o'); ProcessId=$processId; TaskPath=$data.TaskName})
   }
  }
 } catch { $result.Errors.Add('Task Scheduler 129: ' + $_.Exception.Message) }
 return $result
}
function Exe-Path([string]$command) {
 $c=[Environment]::ExpandEnvironmentVariables($command.Trim())
 if($c -match '^"([^"]+)"') { return $matches[1] }
 if($c -match '^(.*?\.(?:exe|com|sys|bat|cmd|ps1|vbs|js))(?=\s|$)') {return $matches[1]}
 return $c
}
function Test-MissingLocalExe([string]$path) {
 if($path -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetExtension($path) -ne '.exe'){return $false}
 $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($path))
 if($drive.DriveType -ne [IO.DriveType]::Fixed){return $false}
 $parent=Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($path)) -ErrorAction Stop
 if(-not $parent.PSIsContainer -or ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)){return $false}
 try {$null=Get-Item -LiteralPath $path -ErrorAction Stop; return $false}
 catch [System.Management.Automation.ItemNotFoundException] {return $true}
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
   Section 'Logon policy' {
    foreach ($hive in @('HKLM:','HKCU:')) {
     $path = $hive + '\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run'
     if (Test-Path -LiteralPath $path) {
      $key = Get-Item -LiteralPath $path
      foreach ($name in $key.GetValueNames()) {
       Add-Item 'policy' $name ([string]$key.GetValue($name)) $path 'Present' 'Logon policy entry. Read-only.'
      }
     }
    }
    $path = 'HKLM:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
    $key = Get-Item -LiteralPath $path
    foreach ($name in @('Shell','Userinit')) {
     $value = $key.GetValue($name)
     if ($value) { Add-Item 'policy' "Winlogon / $name" ([string]$value) $path 'Present' 'Windows logon configuration. Read-only.' }
    }
   }
   Section 'Scheduled tasks' {foreach($t in Get-ScheduledTask){$commands=($t.Actions | ForEach-Object {"$($_.Execute) $($_.Arguments)"}) -join '; '; Add-Item 'task' $t.TaskName $commands $t.TaskPath ([string]$t.State) ("Author: "+$t.Author+"; triggers: "+(($t.Triggers.CimClass.CimClassName) -join ', ')) ($t.TaskPath -notlike '\Microsoft\*' -and $t.Author -notmatch '(?i)Microsoft' -and $t.Settings.Enabled)}}
   Section 'Services' {
    foreach($s in Get-CimInstance Win32_Service){
     Add-Item 'service' $s.Name $s.PathName ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$s.Name) ("$($s.State) / $($s.StartMode)") ("Account: "+$s.StartName+"; "+$s.DisplayName+". Disabling affects future starts, not current execution.") ($s.StartMode -ne 'Disabled' -and $s.PathName -notmatch '(?i)\\Windows\\|Defender|MsMpEng')
     $script:items[$script:items.Count-1].ProcessId=[int]$s.ProcessId
    }
   }
   Section 'Drivers' {foreach($s in Get-CimInstance Win32_SystemDriver){Add-Item 'driver' $s.Name $s.PathName 'Win32_SystemDriver' ("$($s.State) / $($s.StartMode)") 'Kernel driver. Read-only.'}}
   Section 'Startup folders' {$shell=New-Object -ComObject WScript.Shell; foreach($root in Startup-Roots){if(Test-Path -LiteralPath $root){foreach($f in Get-ChildItem -LiteralPath $root -File -Force){$target=$f.FullName;if($f.Extension -eq '.lnk'){$l=$shell.CreateShortcut($f.FullName);$target='"'+$l.TargetPath+'" '+$l.Arguments};Add-Item 'startup' $f.Name $target $f.FullName 'Present' 'Startup folder entry.' ($f.Extension -eq '.lnk')}}}}
   Section 'Processes' {
    $procs=@(Get-CimInstance Win32_Process)
    $map=@{}
    foreach($p in $procs){$map[[int]$p.ProcessId]=$p}
    foreach($p in $procs){
     $parent=$map[[int]$p.ParentProcessId]
     $parentName=if($parent -and $parent.CreationDate -le $p.CreationDate){$parent.Name}else{'unverified'}
     Add-Item 'process' $p.Name $p.CommandLine ("PID "+$p.ProcessId) 'Running' ("Executable: "+$p.ExecutablePath+"; parent PID "+$p.ParentProcessId+" ("+$parentName+").")
     $row=$script:items[$script:items.Count-1]
     $row.Executable=[string]$p.ExecutablePath
     $row.ProcessId=[int]$p.ProcessId
     if($p.CreationDate){$row.ProcessStartedAt=$p.CreationDate.ToUniversalTime().ToString('o')}
    }
   }
   Section 'WMI subscriptions' {foreach($b in Get-CimInstance -Namespace root/subscription -ClassName __FilterToConsumerBinding){Add-Item 'wmi' ([string]$b.Consumer) ([string]$b.Filter) 'root/subscription' 'Persistent binding' 'Permanent WMI subscription; read-only. May be legitimate.'};foreach($c in Get-CimInstance -Namespace root/subscription -ClassName __EventConsumer){Add-Item 'wmi' $c.Name ([string]$c.CommandLineTemplate+[string]$c.ScriptText) 'root/subscription/consumer' 'Consumer' 'Persistent event consumer; read-only.'}}
   Section 'Installed software' {
    $registryRoots = @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
     'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
     'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')
    foreach ($root in $registryRoots) {
     if (-not (Test-Path -LiteralPath $root)) { continue }
     foreach ($key in Get-ChildItem -LiteralPath $root) {
      $entry = Get-ItemProperty -LiteralPath $key.PSPath
      if (-not $entry.DisplayName) { continue }
      Add-Item 'software' $entry.DisplayName $entry.UninstallString $key.Name ([string]$entry.DisplayVersion) ("Publisher: "+$entry.Publisher+"; install: "+$entry.InstallLocation)
      $row = $script:items[$script:items.Count-1]
      $row.Id = "software|$($key.PSPath)"
      $row.Publisher = [string]$entry.Publisher
      $row.InstallLocation = [string]$entry.InstallLocation
      $row.IsMsi = $entry.WindowsInstaller -eq 1
     }
    }
   }
   $softwareObservedAt = [DateTime]::UtcNow.ToString('o')
   $removals = [Collections.Generic.List[object]]::new()
   Section 'MSI removal history' {
    foreach ($event in Read-Events 'Application' 1034 720 500 'MsiInstaller') {
     $fields = $event.Properties
     if ($fields.Count -lt 5 -or [string]$fields[3].Value -ne '0') { continue }
     $removals.Add([ordered]@{
      RecordId=$event.RecordId; Time=$event.TimeCreated.ToUniversalTime().ToString('o')
      Name=[string]$fields[0].Value; Version=[string]$fields[1].Value; Publisher=[string]$fields[4].Value
     })
    }
   }
   Section 'AppX packages' {
    foreach ($package in Get-AppxPackage) {
     Add-Item 'package' $package.Name $package.Publisher $package.InstallLocation ([string]$package.Version) 'Registered AppX/MSIX package for the current user.'
    }
   }
   Section 'TCP connections' {foreach($c in Get-NetTCPConnection -State Established,Listen){Add-Item 'network' ("PID "+$c.OwningProcess) ("$($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort)") ("TCP/"+$c.OwningProcess+"/"+$c.LocalAddress+"/"+$c.LocalPort+"/"+$c.RemoteAddress+"/"+$c.RemotePort) ([string]$c.State) 'Connection is not evidence of malware.'}}
   Section 'UDP endpoints' {
    foreach($u in (Get-NetUDPEndpoint | Sort-Object OwningProcess,LocalAddress,LocalPort -Unique)){
     Add-Item 'network' ("PID "+$u.OwningProcess) ("$($u.LocalAddress):$($u.LocalPort)") ("UDP/"+$u.OwningProcess+"/"+$u.LocalAddress+"/"+$u.LocalPort) 'UDP' 'Local UDP endpoint. No remote peer is inferred.'
    }
   }
   $chromiumRoots = [ordered]@{
    Chrome='Google\Chrome\User Data'
    Edge='Microsoft\Edge\User Data'
    Brave='BraveSoftware\Brave-Browser\User Data'
    Vivaldi='Vivaldi\User Data'
    Yandex='Yandex\YandexBrowser\User Data'
   }
   foreach ($browser in $chromiumRoots.Keys) {
    Section "$browser extensions" {
     $root = Join-Path $env:LOCALAPPDATA $chromiumRoots[$browser]
     if (-not (Test-Path -LiteralPath $root)) { return }
     foreach ($profile in Get-ChildItem -LiteralPath $root -Directory) {
      if ($profile.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
      Section "$browser profile $($profile.Name)" {
       $ext = Join-Path $profile.FullName 'Extensions'
       if (-not (Test-Path -LiteralPath $ext -PathType Container)) { return }
       if ((Get-Item -LiteralPath $ext).Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
       foreach ($id in Get-ChildItem -LiteralPath $ext -Directory -Attributes !ReparsePoint) {
        foreach ($version in Get-ChildItem -LiteralPath $id.FullName -Directory -Attributes !ReparsePoint) {
         $path = Join-Path $version.FullName 'manifest.json'
         if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
         Section "$browser manifest" {
          $file = Get-Item -LiteralPath $path
          if ($file.Length -gt 4MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Skipped manifest.json (size or reparse point).' }
          $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
          Add-Item 'extension' ([string]$manifest.name) (($manifest.permissions + $manifest.host_permissions) -join ', ') $path ([string]$manifest.version) ($browser+' / '+$profile.Name+' / '+$id.Name+'. Folder presence does not prove extension is enabled.')
         }
        }
       }
      }
     }
    }
   }
   Section 'Firefox extensions' {
    $profiles = Join-Path $env:APPDATA 'Mozilla\Firefox\Profiles'
    if (Test-Path -LiteralPath $profiles) {
     foreach ($profile in Get-ChildItem -LiteralPath $profiles -Directory) {
      if ($profile.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
      $path = Join-Path $profile.FullName 'extensions.json'
      if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
      Section 'Firefox profile' {
       $file = Get-Item -LiteralPath $path
       if ($file.Length -gt 16MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Skipped extensions.json (size or reparse point).' }
       $profileData = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
       foreach ($addon in $profileData.addons) {
        if ($addon.type -ne 'extension' -or -not $addon.id) { continue }
        $name = if ($addon.defaultLocale.name) { $addon.defaultLocale.name } else { $addon.id }
        $state = if ($addon.active -eq $true) { 'Active in profile' } elseif ($addon.active -eq $false) { 'Inactive in profile' } else { 'Unknown' }
        Add-Item 'extension' "$name [$($addon.id)]" (($addon.userPermissions.permissions + $addon.userPermissions.origins) -join ', ') $path $state ("Firefox / "+$profile.Name+" / "+$addon.version)
       }
      }
     }
    }
   }
   $launches = Read-Launches
   [ordered]@{Items=@($script:items.ToArray());Errors=@($script:errors.ToArray()); SoftwareObservedAt=$softwareObservedAt; Removals=$removals; Launches=$launches}|ConvertTo-Json -Depth 12 -Compress
  }
  'snapshot' {Snapshot $request.item | ConvertTo-Json -Depth 12 -Compress}
  'launch-evidence' { Read-Launches | ConvertTo-Json -Depth 8 -Compress }
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
