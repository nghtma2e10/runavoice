param(
    [string]$Action = "build",
    [string]$Profile = "debug",
    [switch]$Idle = $false
)

$mPath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
$uPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
$env:Path = "$HOME\.cargo\bin;C:\Program Files\LLVM\bin;$uPath;$mPath;$env:Path"

if ($Idle) {
    [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle
    $env:CARGO_BUILD_JOBS = "2"
}

$engineDir = $PSScriptRoot
Set-Location $engineDir

switch ($Action) {
    "build" {
        if ($Profile -eq "release") {
            cargo build -p runa-engine --release --target x86_64-pc-windows-msvc
        } elseif ($Profile -eq "dev-opt") {
            cargo build -p runa-engine --profile dev-opt --target x86_64-pc-windows-msvc
        } else {
            cargo build -p runa-engine --target x86_64-pc-windows-msvc
        }
    }
    "header" {
        cbindgen --config cbindgen.toml --crate runa-engine --output include/runa_engine.h
    }
    "test" {
        cargo test
        cargo run -p runa-engine-cli
    }
    "clippy" {
        cargo clippy --all-targets -- -D warnings
    }
    "deny" {
        cargo deny check
    }
    "audit" {
        cargo audit
    }
    default {
        Write-Error "Unknown action: $Action"
    }
}
