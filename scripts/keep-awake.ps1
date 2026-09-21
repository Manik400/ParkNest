# Keeps ParkNest Render instance awake by pinging /health every 10 seconds.
# Runs continuously in the background.

param(
    [string]$Url = 'https://parknest.onrender.com/health',
    [int]$TimeoutSeconds = 90
)

$logDir = Join-Path $env:LOCALAPPDATA 'ParkNest'
$log = Join-Path $logDir 'keep-awake.log'

New-Item -ItemType Directory -Force $logDir | Out-Null

while ($true) {

    $started = Get-Date

    try {
        $response = Invoke-WebRequest `
            -Uri $Url `
            -UseBasicParsing `
            -TimeoutSec $TimeoutSeconds

        $line = '{0:yyyy-MM-dd HH:mm:ss}  OK    {1}  {2:N1}s' -f `
            $started,
            $response.StatusCode,
            ((Get-Date) - $started).TotalSeconds
    }
    catch {
        $line = '{0:yyyy-MM-dd HH:mm:ss}  FAIL  {1:N1}s  {2}' -f `
            $started,
            ((Get-Date) - $started).TotalSeconds,
            $_.Exception.Message
    }

    # Keep approximately the last 1 day of logs
    $lines = @()
    if (Test-Path $log) {
        $lines = @(Get-Content $log -Tail 8639)
    }

    Set-Content `
        -Path $log `
        -Value ($lines + $line) `
        -Encoding utf8

    # Console output is useful when manually testing
    Write-Output $line

    # Wait 10 seconds before next ping
    Start-Sleep -Seconds 10
}