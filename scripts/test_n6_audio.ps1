# scripts/test_n6_audio.ps1
# N6 Testi: Gerçek Ses & Audio Track Doğrulaması (Ana Plan 15.4)
# İki örnek --engine=rust bağlanır, karşılıklı Audio track'lerin açıldığı ve ses akışının aktif olduğu doğrulanır.

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$exePath = Join-Path $projectRoot "bin\Release\net8.0-windows10.0.26100.0\win-x64\RunaVoice.WinUI.exe"

$profA = "AD_A"
$profB = "AD_B"
$dirA = Join-Path $env:LOCALAPPDATA ("RunaVoice_" + $profA)
$dirB = Join-Path $env:LOCALAPPDATA ("RunaVoice_" + $profB)

# 1. Clean old test profile directories
if (Test-Path $dirA) { Remove-Item -Recurse -Force $dirA -ErrorAction SilentlyContinue }
if (Test-Path $dirB) { Remove-Item -Recurse -Force $dirB -ErrorAction SilentlyContinue }

# 2. Kill existing test processes
Get-Process "RunaVoice.WinUI" -ErrorAction SilentlyContinue | Where-Object {
    try {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $($_.Id)").CommandLine
        $cmd -like "*AD_A*" -or $cmd -like "*AD_B*"
    } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 500

$roomCode = "RUNA-9860"
Write-Host "Launching Instance A and Instance B in Room: $roomCode..."

$procA = Start-Process -FilePath $exePath -ArgumentList "--engine=rust", "--profile=$profA", "--join=$roomCode" -PassThru
Start-Sleep -Seconds 2
$procB = Start-Process -FilePath $exePath -ArgumentList "--engine=rust", "--profile=$profB", "--join=$roomCode" -PassThru

$start = [System.Diagnostics.Stopwatch]::StartNew()
$connected = $false
$hasConnA = $false
$hasConnB = $false
$audioTrackA = $false
$audioTrackB = $false

while ($start.Elapsed.TotalSeconds -lt 25) {
    Start-Sleep -Milliseconds 500

    $logFilesA = Get-ChildItem (Join-Path $dirA "logs") -Filter "engine.log*" -ErrorAction SilentlyContinue
    $logA = if ($logFilesA) { $logFilesA[0].FullName } else { $null }

    $logFilesB = Get-ChildItem (Join-Path $dirB "logs") -Filter "engine.log*" -ErrorAction SilentlyContinue
    $logB = if ($logFilesB) { $logFilesB[0].FullName } else { $null }

    if ($logA -and (Test-Path $logA)) {
        $cA = Get-Content $logA -Raw -ErrorAction SilentlyContinue
        if ($cA -match "State -> connected") { $hasConnA = $true }
        if ($cA -match "Audio track received") { $audioTrackA = $true }
    }
    if ($logB -and (Test-Path $logB)) {
        $cB = Get-Content $logB -Raw -ErrorAction SilentlyContinue
        if ($cB -match "State -> connected") { $hasConnB = $true }
        if ($cB -match "Audio track received") { $audioTrackB = $true }
    }

    if ($hasConnA -and $hasConnB -and $audioTrackA -and $audioTrackB) {
        $connected = $true
        Write-Host "  -> [GREEN] P2P Connected and Bidirectional Audio Tracks established at $($start.Elapsed.TotalSeconds.ToString('F2'))s!" -ForegroundColor Green
        break;
    }
}

Write-Host "`n--- N6 AUDIO VERIFICATION RESULTS ---"
Write-Host "Instance A Connected: $hasConnA | Audio Track Received: $audioTrackA"
Write-Host "Instance B Connected: $hasConnB | Audio Track Received: $audioTrackB"
Write-Host "Bidirectional Audio Pipeline Connected: $connected"

if (-not $connected) {
    Write-Host "`n--- LOG A TAIL ---"
    if ($logA) { Get-Content $logA -Tail 30 | Write-Host }
    Write-Host "`n--- LOG B TAIL ---"
    if ($logB) { Get-Content $logB -Tail 30 | Write-Host }
}

# Clean processes
try { Stop-Process -Id $procA.Id -Force -ErrorAction SilentlyContinue } catch {}
try { Stop-Process -Id $procB.Id -Force -ErrorAction SilentlyContinue } catch {}

# Clean test profiles
if (Test-Path $dirA) { Remove-Item -Recurse -Force $dirA -ErrorAction SilentlyContinue }
if (Test-Path $dirB) { Remove-Item -Recurse -Force $dirB -ErrorAction SilentlyContinue }

if ($connected) {
    Write-Host "`n>>> [SUCCESS] N6 REAL AUDIO VERIFICATION PASSED! <<<" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n>>> [FAILED] N6 Audio verification failed <<<" -ForegroundColor Red
    exit 1
}
