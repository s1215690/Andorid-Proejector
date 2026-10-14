$exe = Join-Path $env:USERPROFILE 'android-cast\AndroidCast.exe'
$action = New-ScheduledTaskAction -Execute $exe -Argument '--bg'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
Register-ScheduledTask -TaskName '無線投屏-後台' -Action $action -Trigger $trigger -Description '無線投屏常駐後台（開機自動、無視窗）' -Force | Out-Null
Start-ScheduledTask -TaskName '無線投屏-後台'
Start-Sleep -Seconds 6
Get-ScheduledTask -TaskName '無線投屏-後台' | Select-Object TaskName,State | Format-List
