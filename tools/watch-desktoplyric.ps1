# Watch DesktopLyric. Does not restart it.
# Logs to %AppData%\DesktopLyric\watch.log
# Ctrl+C to stop.

$appData = Join-Path $env:APPDATA "DesktopLyric"
New-Item -ItemType Directory -Force -Path $appData | Out-Null
$watchLog = Join-Path $appData "watch.log"
$runLog = Join-Path $appData "run.log"
$errorLog = Join-Path $appData "error.log"

function Log([string]$msg) {
    $line = "$(Get-Date -Format 's') $msg"
    Add-Content -Path $watchLog -Value $line -Encoding UTF8
    Write-Host $line
}

Log "watch-start"
$lastPid = $null
$wasAlive = $false
while ($true) {
    $p = Get-Process DesktopLyric -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        if (-not $wasAlive) {
            Log "alive pid=$($p.Id) start=$($p.StartTime.ToString('s')) ws=$([int]($p.WorkingSet64/1MB))MB"
            $lastPid = $p.Id
        }
        $wasAlive = $true
        $lastPid = $p.Id
    }
    elseif ($wasAlive) {
        $wasAlive = $false
        Log "DEAD last-pid=$lastPid (no process)"
        if (Test-Path $runLog) {
            Log "--- run.log tail ---"
            Get-Content $runLog -Tail 12 | ForEach-Object { Log "  $_" }
        }
        if (Test-Path $errorLog) {
            $err = Get-Item $errorLog
            Log "error.log LastWrite=$($err.LastWriteTime.ToString('s'))"
        }
        Log "check Event Viewer Application for DesktopLyric / .NET Runtime / 0xc0000374"
    }
    Start-Sleep -Seconds 3
}
