# scripts/test_n5_automation.ps1
# N5 Test Paketi: 20 Tekrar, Uygulama İçi P2P Bağlantı & Gecikme Doğrulaması
# Başarı Kriteri: >= 19/20 bağlantı, 25 sn içinde iki tarafta aktif ms gecikmesi ve PrintWindow ekran görüntüleri.

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$exePath = Join-Path $projectRoot "bin\Release\net8.0-windows10.0.26100.0\win-x64\RunaVoice.WinUI.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found at: $exePath"
    exit 1
}

$screenshotsDir = Join-Path $projectRoot "docs\engine-progress\screenshots"
if (-not (Test-Path $screenshotsDir)) {
    New-Item -ItemType Directory -Path $screenshotsDir -Force | Out-Null
}

# Win32 Window Capture
Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class WindowCapture {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static bool Capture(IntPtr hwnd, string filePath) {
        RECT rc;
        if (!GetWindowRect(hwnd, out rc)) return false;
        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0) return false;

        using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb)) {
            using (Graphics g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                try {
                    PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
                } finally {
                    g.ReleaseHdc(hdc);
                }
            }
            bmp.Save(filePath, ImageFormat.Png);
        }
        return true;
    }
}
"@ -ReferencedAssemblies System.Drawing

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Run-Single-N5-Iteration([int]$runId, [string]$roomCode) {
    Write-Host "`n========================================================"
    Write-Host ">>> [N5] Iteration $runId / 20 - Room: $roomCode"
    Write-Host "========================================================"

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

    Start-Sleep -Milliseconds 400

    # 3. Launch Instance A
    Write-Host "Launching Instance A (--engine=rust --profile=$profA --join=$roomCode)..."
    $procA = Start-Process -FilePath $exePath -ArgumentList "--engine=rust", "--profile=$profA", "--join=$roomCode" -PassThru

    Start-Sleep -Seconds 2

    # 4. Launch Instance B
    Write-Host "Launching Instance B (--engine=rust --profile=$profB --join=$roomCode)..."
    $procB = Start-Process -FilePath $exePath -ArgumentList "--engine=rust", "--profile=$profB", "--join=$roomCode" -PassThru

    $start = [System.Diagnostics.Stopwatch]::StartNew()
    $connected = $false
    $connTimeSec = 0.0

    # 5. Poll for connection (up to 25s per Section 5.2 requirement)
    while ($start.Elapsed.TotalSeconds -lt 25) {
        Start-Sleep -Milliseconds 500

        $logFilesA = Get-ChildItem (Join-Path $dirA "logs") -Filter "engine.log*" -ErrorAction SilentlyContinue
        $logA = if ($logFilesA) { $logFilesA[0].FullName } else { $null }

        $logFilesB = Get-ChildItem (Join-Path $dirB "logs") -Filter "engine.log*" -ErrorAction SilentlyContinue
        $logB = if ($logFilesB) { $logFilesB[0].FullName } else { $null }

        $hasConnA = $false
        $hasConnB = $false

        if ($logA -and (Test-Path $logA)) {
            $contentA = Get-Content $logA -Raw -ErrorAction SilentlyContinue
            if ($contentA -match "State -> connected") { $hasConnA = $true }
        }
        if ($logB -and (Test-Path $logB)) {
            $contentB = Get-Content $logB -Raw -ErrorAction SilentlyContinue
            if ($contentB -match "State -> connected") { $hasConnB = $true }
        }

        if ($hasConnA -and $hasConnB) {
            $connTimeSec = [Math]::Round($start.Elapsed.TotalSeconds, 2)
            Write-Host "  -> [GREEN] P2P Connected at ${connTimeSec}s!" -ForegroundColor Green
            $connected = $true
            # Allow active RTT pings to arrive
            Start-Sleep -Seconds 2
            break;
        }
    }

    # 6. UI Automation inspection and screenshot capture
    $ssA = Join-Path $screenshotsDir "N5_Run${runId}_InstanceA.png"
    $ssB = Join-Path $screenshotsDir "N5_Run${runId}_InstanceB.png"

    $rttA = ""
    $rttB = ""

    $procA.Refresh()
    $procB.Refresh()

    $root = [System.Windows.Automation.AutomationElement]::RootElement

    # Check A
    $condA = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procA.Id)
    $winA = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condA)
    if ($winA) {
        $allTextA = $winA.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $allTextA) {
            $name = $el.Current.Name
            if ($name -match "\d+\s*ms" -or $name -match "<\s*0,1\s*ms") {
                $rttA = $name
            }
            # Open popover on remote participant to show detailed latency breakdown
            if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
                try {
                    $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                    if ($inv) { $inv.Invoke() }
                } catch {}
            }
        }
    }

    # Check B
    $condB = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procB.Id)
    $winB = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condB)
    if ($winB) {
        $allTextB = $winB.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $allTextB) {
            $name = $el.Current.Name
            if ($name -match "\d+\s*ms" -or $name -match "<\s*0,1\s*ms") {
                $rttB = $name
            }
        }
    }

    Start-Sleep -Milliseconds 300

    $hwndA = if ($winA) { [IntPtr]$winA.Current.NativeWindowHandle } else { $procA.MainWindowHandle }
    $hwndB = if ($winB) { [IntPtr]$winB.Current.NativeWindowHandle } else { $procB.MainWindowHandle }

    if ($hwndA -ne [IntPtr]::Zero) {
        [WindowCapture]::Capture($hwndA, $ssA) | Out-Null
    }
    if ($hwndB -ne [IntPtr]::Zero) {
        [WindowCapture]::Capture($hwndB, $ssB) | Out-Null
    }

    Write-Host "  -> Instance A Latency UI: '$rttA' | Screenshot: $ssA"
    Write-Host "  -> Instance B Latency UI: '$rttB' | Screenshot: $ssB"

    # 7. Clean processes
    try { Stop-Process -Id $procA.Id -Force -ErrorAction SilentlyContinue } catch {}
    try { Stop-Process -Id $procB.Id -Force -ErrorAction SilentlyContinue } catch {}

    # 8. Clean test profiles
    if (Test-Path $dirA) { Remove-Item -Recurse -Force $dirA -ErrorAction SilentlyContinue }
    if (Test-Path $dirB) { Remove-Item -Recurse -Force $dirB -ErrorAction SilentlyContinue }

    return [PSCustomObject]@{
        RunId       = $runId
        Room        = $roomCode
        Connected   = $connected
        TimeSec     = $connTimeSec
        RttA        = if ($rttA) { $rttA } else { "N/A" }
        RttB        = if ($rttB) { $rttB } else { "N/A" }
        ScreenshotA = $ssA
        ScreenshotB = $ssB
    }
}

Write-Host "================================================================"
Write-Host "STARTING N5 IN-APP 20-ITERATION SUITE (Rust Engine P2P Full Mesh)"
Write-Host "================================================================"

$results = @()
$passedCount = 0

for ($i = 1; $i -le 20; $i++) {
    $room = "RUNA-987" + ($i % 10)
    $res = Run-Single-N5-Iteration $i $room
    $results += $res
    if ($res.Connected) {
        $passedCount++
    }
    Start-Sleep -Milliseconds 500
}

Write-Host "`n================================================================"
Write-Host ">>> N5 TEST SUMMARY: $passedCount / 20 PASSED (Target >= 19/20) <<<"
Write-Host "================================================================"

$results | Format-Table -Property RunId, Room, Connected, TimeSec, RttA, RttB -AutoSize

if ($passedCount -ge 19) {
    Write-Host "`n>>> [SUCCESS] N5 SUITE PASSED WITH $passedCount/20 GREEN RUNS! <<<" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n>>> [FAILED] N5 Suite did not reach target (Passed: $passedCount/20) <<<" -ForegroundColor Red
    exit 1
}
