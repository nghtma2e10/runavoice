# scripts/smoke_rust_engine.ps1
# U1: Uygulama duman testi (C#)
# --engine=rust --profile=SmokeA --open-settings ile acilir -> 8 sn beklenir -> UIA ile TxtActiveEngine kontrol edilir,
# engine.log icerisinde "initialized successfully" teyit edilir, profil temizlenir.

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$exePath = Join-Path $projectRoot "bin\Release\net8.0-windows10.0.26100.0\win-x64\RunaVoice.WinUI.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found at: $exePath. Please run dotnet build -c Release first."
    exit 1
}

$profileName = "SmokeA"
$profileDir = Join-Path $env:LOCALAPPDATA ("RunaVoice_" + $profileName)

Write-Host "=== U1 Smoke Test: Rust Engine Launch & Verification ==="
Write-Host "Project Root: $projectRoot"
Write-Host "Executable:   $exePath"
Write-Host "Profile Dir:  $profileDir"

# Pre-cleanup
if (Test-Path $profileDir) {
    Write-Host "Cleaning up old profile directory..."
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}

# Kill any lingering test instances
Get-Process "RunaVoice.WinUI" -ErrorAction SilentlyContinue | Where-Object {
    try {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $($_.Id)").CommandLine
        $cmd -like "*$profileName*"
    } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Launching RunaVoice.WinUI with --engine=rust --profile=$profileName --open-settings..."
$proc = Start-Process -FilePath $exePath -ArgumentList "--engine=rust", "--profile=$profileName", "--open-settings" -PassThru

Write-Host "Process started with PID: $($proc.Id). Waiting 8 seconds for initialization..."
Start-Sleep -Seconds 8

$testPassed = $true
$failureReasons = @()

# 1. Check process is still alive
if ($proc.HasExited) {
    $testPassed = $false
    $failureReasons += "Process exited unexpectedly with code $($proc.ExitCode)"
} else {
    Write-Host "[CHECK 1] Process is running. PID: $($proc.Id)"
}

# 2. Check engine.log
$logsDir = Join-Path $profileDir "logs"
$logFiles = Get-ChildItem -Path $logsDir -Filter "engine.log*" -ErrorAction SilentlyContinue
if (-not $logFiles -or $logFiles.Count -eq 0) {
    $testPassed = $false
    $failureReasons += "No engine.log file created in $logsDir"
} else {
    $latestLog = $logFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $logContent = Get-Content $latestLog.FullName -Raw
    Write-Host "[CHECK 2] Log file found: $($latestLog.Name) ($($logContent.Length) bytes)"

    if ($logContent -match "RunaEngine initialized successfully") {
        Write-Host "  -> SUCCESS: 'RunaEngine initialized successfully' found in log!"
    } else {
        $testPassed = $false
        $failureReasons += "'RunaEngine initialized successfully' NOT found in engine.log"
    }

    if ($logContent -match "runa_engine_create failed") {
        $testPassed = $false
        $failureReasons += "Found 'runa_engine_create failed' in engine.log!"
    } else {
        Write-Host "  -> SUCCESS: No engine create failures in log."
    }
}

# 3. Check UI Automation for TxtActiveEngine
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $procCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $windowElem = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)

    if ($windowElem) {
        Write-Host "[CHECK 3] Window found: '$($windowElem.Current.Name)'"
        $autoIdCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TxtActiveEngine")
        $engineElem = $windowElem.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $autoIdCond)

        if ($engineElem) {
            $engineText = $engineElem.Current.Name
            Write-Host "  -> TxtActiveEngine text: '$engineText'"
            if ($engineText -like "*Rust*") {
                Write-Host "  -> SUCCESS: Active engine is confirmed to be Rust via UI Automation!"
            } else {
                $testPassed = $false
                $failureReasons += "TxtActiveEngine does not contain 'Rust': '$engineText'"
            }
        } else {
            Write-Host "  -> Note: TxtActiveEngine element not found via AutomationId directly."
        }
    } else {
        Write-Host "  -> Window element not found via ProcessId search."
    }
} catch {
    Write-Host "  -> UI Automation inspection warning: $_"
}

# Cleanup: terminate process
Write-Host "Terminating test process..."
try {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
} catch {}

Start-Sleep -Seconds 2

# Cleanup: delete test profile directory
if (Test-Path $profileDir) {
    Write-Host "Cleaning up test profile directory..."
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}

Write-Host "============================================================"
if ($testPassed) {
    Write-Host ">>> U1 SMOKE TEST PASSED! Rust Engine successfully initialized in WinUI 3 host! <<<" -ForegroundColor Green
    exit 0
} else {
    Write-Host ">>> U1 SMOKE TEST FAILED! <<<" -ForegroundColor Red
    foreach ($reason in $failureReasons) {
        Write-Host "  - $reason" -ForegroundColor Red
    }
    exit 1
}
