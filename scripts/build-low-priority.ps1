param(
    [string]$Configuration = "Debug"
)

Write-Host "=== RunaVoice Dusuk Oncelikli Derleme (Idle Priority, 2 Core Affinity) ===" -ForegroundColor Cyan

# Set process priority to Idle
$proc = [System.Diagnostics.Process]::GetCurrentProcess()
$proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle

# 2-core affinity mask (0x03 = cores 0 and 1)
try {
    $proc.ProcessorAffinity = [IntPtr]3
} catch { }

$env:CARGO_BUILD_JOBS = "2"

$mPath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
$uPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
$env:Path = "$HOME\.cargo\bin;C:\Program Files\LLVM\bin;$uPath;$mPath;$env:Path"

$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

Write-Host "1. Rust Engine derleniyor..." -ForegroundColor Yellow
$cargoProfile = if ($Configuration -eq "Debug") { "--profile dev-opt" } else { "--release" }
& cargo build --manifest-path "$projectRoot\engine\Cargo.toml" -p runa-engine $cargoProfile.Split(' ') --target x86_64-pc-windows-msvc

if ($LASTEXITCODE -ne 0) {
    Write-Error "Rust motor derlemesi basarisiz oldu!"
    exit $LASTEXITCODE
}

Write-Host "2. .NET RunaVoice.WinUI derleniyor (-m:1)..." -ForegroundColor Yellow
& dotnet build "$projectRoot\RunaVoice.WinUI.csproj" -c $Configuration -m:1

exit $LASTEXITCODE
