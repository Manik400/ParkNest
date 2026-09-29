# Registers "ParkNestKeepAwake": runs keep-awake.ps1 hidden, forever, whenever this laptop is in use.
#
# It starts when you log in, when you unlock, and when the laptop wakes from sleep, and a 5-minute
# watchdog starts it again if it was ever stopped. Only one copy runs at a time (the others are
# ignored while it is running). It is launched through keep-awake.vbs, so no window ever appears,
# and it never wakes a sleeping laptop by itself.
#
# Run:
#   powershell -ExecutionPolicy Bypass -File scripts\schedule-keep-awake.ps1
#
# Remove:
#   powershell -ExecutionPolicy Bypass -File scripts\schedule-keep-awake.ps1 -Remove

param([switch]$Remove)

$name = 'ParkNestKeepAwake'

if ($Remove) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object CommandLine -like '*keep-awake.ps1*' |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Write-Output "Removed $name."
    return
}

$vbs = Join-Path $PSScriptRoot 'keep-awake.vbs'
$user = "$env:USERDOMAIN\$env:USERNAME"
$start = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')

# Task XML, because the unlock and wake-from-sleep triggers have no New-ScheduledTaskTrigger form.
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Keeps the ParkNest Render instance awake by pinging /health every few seconds while this PC is in use.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$user</UserId>
    </LogonTrigger>
    <SessionStateChangeTrigger>
      <Enabled>true</Enabled>
      <StateChange>SessionUnlock</StateChange>
      <UserId>$user</UserId>
    </SessionStateChangeTrigger>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
    <TimeTrigger>
      <Enabled>true</Enabled>
      <StartBoundary>$start</StartBoundary>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$env:WINDIR\System32\wscript.exe</Command>
      <Arguments>"$vbs"</Arguments>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $name -Xml $xml -Force | Out-Null
Start-ScheduledTask -TaskName $name

Write-Output "Registered and started $name. Log: $env:LOCALAPPDATA\ParkNest\keep-awake.log"
