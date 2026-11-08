$cargoBin = "$HOME\.cargo\bin"
if (-not ($env:Path -split ';' -contains $cargoBin)) {
    $env:Path = "$cargoBin;$env:Path"
}

$engineDir = Join-Path (Split-Path -Parent $PSScriptRoot) "engine"
Set-Location $engineDir

& "$cargoBin\cargo.exe" $args
exit $LASTEXITCODE
