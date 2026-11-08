$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "engine\target\release\runa_engine.dll"
$hash = (Get-FileHash -Path $src -Algorithm SHA256).Hash.ToLower()
Write-Host "New DLL SHA256: $hash"

$targets = @(
    (Join-Path $root "bin\Release\net8.0-windows10.0.26100.0\win-x64"),
    (Join-Path $root "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"),
    (Join-Path $root "bin\Release\net8.0-windows10.0.19041.0\win-x64"),
    (Join-Path $root "artifacts\publish")
)

foreach ($t in $targets) {
    if (Test-Path $t) {
        Copy-Item -Path $src -Destination (Join-Path $t "runa_engine.dll") -Force
        [IO.File]::WriteAllText((Join-Path $t "runa_engine.dll.sha256"), $hash, (New-Object Text.UTF8Encoding $false))
        Write-Host "Updated: $t"
    }
}
