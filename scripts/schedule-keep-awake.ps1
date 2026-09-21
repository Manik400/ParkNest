# Registers "ParkNestKeepAwake".
# Starts keep-awake.ps1 automatically when you log into Windows.
#
# Run:
#   powershell -ExecutionPolicy Bypass -File scripts\schedule-keep-awake.ps1
#
# Remove:
#   powershell -ExecutionPolicy Bypass -File scripts\schedule-keep-awake.ps1 -Remove

param([switch]$Remove)

$name = 'ParkNestKeepAwake'

if ($Remove) {
    Unregister-ScheduledTask `
        -TaskName $name `
        -Confirm:$false `
        -ErrorAction SilentlyContinue

    Write-Output "Removed $name."
    return
}

$script = Join-Path $PSScriptRoot 'keep-awake.ps1'

# Start PowerShell and run keep-awake.ps1 hidden
$action = New-ScheduledTaskAction `
    -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""

# Start automatically when user logs in
$trigger = New-ScheduledTaskTrigger `
    -AtLogOn `
    -User "$env:USERDOMAIN\$env:USERNAME"

# Keep running even on battery and don't stop when switching to battery.
# ExecutionTimeLimit = 0 means unlimited execution time.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -WakeToRun `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Limited

Register-ScheduledTask `
    -TaskName $name `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description 'Keeps ParkNest Render instance awake by pinging /health every 10 seconds.' `
    -Force | Out-Null

Write-Output "Registered $name."
Write-Output "Script: $script"
Write-Output "Log: $env:LOCALAPPDATA\ParkNest\keep-awake.log"