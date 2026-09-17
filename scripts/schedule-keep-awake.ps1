# Registers "ParkNestKeepAwake": pings https://parknest.onrender.com/health every minute while you
# are logged in to this PC, hidden. Re-run to update; -Remove to delete.
#   powershell -ExecutionPolicy Bypass -File scripts\schedule-keep-awake.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\schedule-keep-awake.ps1 -Remove
param([switch]$Remove)

$name = 'ParkNestKeepAwake'

if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed $name."
    return
}

$vbs = Join-Path $PSScriptRoot 'keep-awake.vbs'
$action = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\wscript.exe" -Argument "//B //Nologo `"$vbs`""

$every = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(5) -RepetitionInterval (New-TimeSpan -Minutes 1)
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$atLogon.Repetition = $every.Repetition

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2)

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger @($every, $atLogon) -Settings $settings -Principal $principal `
    -Description 'Pings ParkNest /health every minute so the Render free instance stays awake.' -Force | Out-Null

Write-Output "Registered $name. Log: $env:LOCALAPPDATA\ParkNest\keep-awake.log"
