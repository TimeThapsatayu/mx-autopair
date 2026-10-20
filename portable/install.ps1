# Copies MxAutoPair.exe and registers it to run hidden at logon. No .NET install needed.
$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'MxAutoPair'
$exe = Join-Path $installDir 'MxAutoPair.exe'

# Remove the old task first so its every-minute relaunch can't restart the exe mid-update
Unregister-ScheduledTask -TaskName 'MxAutoPair' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process MxAutoPair -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$PSScriptRoot\MxAutoPair.exe" $exe -Force

$action   = New-ScheduledTaskAction -Execute $exe
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# Every minute: relaunches it if it was closed or crashed (a running copy makes this a no-op)
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
              -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'MxAutoPair' -Action $action -Trigger @($trigger, $watchdog) -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'MxAutoPair'

Write-Host "Installed. Log: $installDir\MxAutoPair.log"
