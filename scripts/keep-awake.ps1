# Pings the hosted ParkNest once so Render's free instance never reaches its 15-minute idle sleep.
# Task Scheduler runs this every minute (see schedule-keep-awake.ps1); run it by hand to test.
param(
    [string]$Url = 'https://parknest.onrender.com/health',
    [int]$TimeoutSeconds = 90
)

$logDir = Join-Path $env:LOCALAPPDATA 'ParkNest'
$log = Join-Path $logDir 'keep-awake.log'
New-Item -ItemType Directory -Force $logDir | Out-Null

$started = Get-Date
try {
    # A sleeping instance takes 30-60 s to answer, hence the long timeout.
    $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSeconds
    $line = '{0:yyyy-MM-dd HH:mm:ss}  OK    {1}  {2:N1}s' -f $started, $response.StatusCode, ((Get-Date) - $started).TotalSeconds
    $exit = 0
}
catch {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  FAIL  {1:N1}s  {2}' -f $started, ((Get-Date) - $started).TotalSeconds, $_.Exception.Message
    $exit = 1
}

# Keep about a day of history.
$lines = @()
if (Test-Path $log) { $lines = @(Get-Content $log -Tail 1439) }
Set-Content -Path $log -Value ($lines + $line) -Encoding utf8

Write-Output $line
exit $exit
