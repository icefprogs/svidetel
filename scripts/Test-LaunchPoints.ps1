$ErrorActionPreference='Stop'
$OutputEncoding=New-Object System.Text.UTF8Encoding($false)
$root=Split-Path $PSScriptRoot -Parent
$bridge=Join-Path $root 'Svidetel/Bridge.ps1'
$exe=Join-Path $root 'tests/bin/Release/net9.0-windows/Svidetel.Tests.exe'
$ps=Join-Path $env:windir 'System32/WindowsPowerShell/v1.0/powershell.exe'
$env:PSModulePath=(Join-Path $env:windir 'System32/WindowsPowerShell/v1.0/Modules')
Import-Module ScheduledTasks
function Call($payload){
 $p=New-Object Diagnostics.Process;$p.StartInfo.FileName=$ps;$p.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$bridge+'"';$p.StartInfo.UseShellExecute=$false;$p.StartInfo.CreateNoWindow=$true;$p.StartInfo.RedirectStandardInput=$true;$p.StartInfo.RedirectStandardOutput=$true;$p.StartInfo.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
 $null=$p.Start();$json=$payload|ConvertTo-Json -Depth 15 -Compress
 $json=[regex]::Replace($json,'[^\x00-\x7F]',{param($m) '\u'+([int][char]$m.Value).ToString('x4')})
 $p.StandardInput.WriteLine($json);$p.StandardInput.Close();$result=$p.StandardOutput.ReadToEnd();$p.WaitForExit();if($p.ExitCode){throw 'Bridge failed'};$p.Dispose();return ($result|ConvertFrom-Json)
}
$name='Svidetel_Test_'+[Guid]::NewGuid().ToString('N')
$shortcut=Join-Path ([Environment]::GetFolderPath('Startup')) ($name+'.lnk')
$registered=$false
try {
 $shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($shortcut);$link.TargetPath=$exe;$link.Arguments='--fixture';$link.Save()
 $item=@{Kind='startup';Name=($name+'.lnk');Location=$shortcut}
 $before=Call @{op='snapshot';item=$item};$null=Call @{op='disable';item=$item;expected=$before}
 if(Test-Path -LiteralPath $shortcut){throw 'Shortcut still present'}
 $null=Call @{op='restore';item=$item;expected=$before}
 if((Get-FileHash -LiteralPath $shortcut).Hash -ne $before.Hash){throw 'Shortcut differs'}
 Write-Output 'PASS shortcut exact-byte disable and restore'
 $action=New-ScheduledTaskAction -Execute $exe -Argument '--fixture'
 $task=New-ScheduledTask -Action $action -Description 'Temporary Witness fixture, no triggers.'
 $null=Register-ScheduledTask -TaskName $name -TaskPath '\' -InputObject $task;$registered=$true
 $item=@{Kind='task';Name=$name;Location='\'}
 $before=Call @{op='snapshot';item=$item};$null=Call @{op='disable';item=$item;expected=$before}
 if((Get-ScheduledTask -TaskName $name -TaskPath '\').Settings.Enabled){throw 'Task still enabled'}
 $null=Call @{op='restore';item=$item;expected=$before}
 if(-not(Get-ScheduledTask -TaskName $name -TaskPath '\').Settings.Enabled){throw 'Task not restored'}
 Write-Output 'PASS scheduled task disable and restore'
} finally {
 if(Test-Path -LiteralPath $shortcut){Remove-Item -LiteralPath $shortcut -Force}
 if($registered){Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false}
}
