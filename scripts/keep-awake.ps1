# Keeps ParkNest Render instance awake by pinging /health every 10 seconds.
# Runs continuously in the background.

param(
    [string]$Url = 'https://parknest.onrender.com/health',
    [int]$TimeoutSeconds = 90,
    [int]$RotateHours = 6,
    [int]$KeepArchiveHours = 24
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

    # Every 6 hours the log is moved to an archive file, and archives older than 1 day are deleted.
    # Best effort: a full disk or a locked file must never stop the pinging, which is the only part
    # that matters.
    try {
        if (Test-Path $log) {
            $first = Get-Content $log -TotalCount 1 -ErrorAction Stop
            $firstTime = [datetime]::MinValue
            if ($first -and [datetime]::TryParseExact($first.Substring(0, [Math]::Min(19, $first.Length)),
                    'yyyy-MM-dd HH:mm:ss', $null, 'None', [ref]$firstTime) -and
                    ($started - $firstTime) -ge [timespan]::FromHours($RotateHours)) {
                $archive = Join-Path $logDir ('keep-awake-{0:yyyyMMdd-HHmmss}.log' -f $started)
                Move-Item $log $archive -ErrorAction Stop
            }
        }

        Get-ChildItem $logDir -Filter 'keep-awake-*.log' -ErrorAction Stop |
            Where-Object LastWriteTime -lt $started.AddHours(-$KeepArchiveHours) |
            Remove-Item -Force -ErrorAction SilentlyContinue

        Add-Content -Path $log -Value $line -Encoding utf8 -ErrorAction Stop
    }
    catch {
    }

    # Console output is useful when manually testing
    Write-Output $line

    # Wait 10 seconds before next ping
    Start-Sleep -Seconds 10
}