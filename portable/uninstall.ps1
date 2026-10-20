# Removes the MxAutoPair startup task and installed files.
Unregister-ScheduledTask -TaskName 'MxAutoPair' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process MxAutoPair -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2  # let the exe release its files
Remove-Item (Join-Path $env:LOCALAPPDATA 'MxAutoPair') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Uninstalled.'
