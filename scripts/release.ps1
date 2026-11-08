<#
  Runa Voice - surum paketleme ve GitHub'a yayinlama.

  Kullanim (proje klasorunden):
    .\scripts\release.ps1                # derle + paketle + GitHub'a yukle
    .\scripts\release.ps1 -SkipUpload    # sadece yerelde paketle (artifacts\Releases)

  Surum numarasi RunaVoice.WinUI.csproj icindeki <Version> degerinden okunur;
  yayin oncesi o degeri artirin. Repo adresi Services\UpdateService.cs icindeki RepoUrl'den okunur.
  Gereken araclar: dotnet, vpk (dotnet tool install -g vpk), gh (yukleme icin, `gh auth login`).
#>
param(
    [switch]$SkipUpload
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- Surum ve repo adresini oku ---
$csproj = Get-Content "$root\RunaVoice.WinUI.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "csproj icinde <Version> bulunamadi." }
$version = $Matches[1].Trim()

$svc = Get-Content "$root\Services\UpdateService.cs" -Raw
if ($svc -notmatch 'RepoUrl\s*=\s*"([^"]+)"') { throw "UpdateService.cs icinde RepoUrl bulunamadi." }
$repo = $Matches[1]
if ($repo -match 'KULLANICI_ADI') {
    if ($SkipUpload) { Write-Warning "RepoUrl henuz ayarlanmamis (yalnizca yerel paketleme yapilacak)." }
    else { throw "Services\UpdateService.cs icindeki RepoUrl'i kendi GitHub adresinizle degistirin." }
}

Write-Host "== Surum $version | Repo $repo ==" -ForegroundColor Cyan

# --- Araclar ---
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw "vpk bulunamadi: dotnet tool install -g vpk" }
if (-not $SkipUpload -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "gh bulunamadi: winget install GitHub.cli ; sonra gh auth login" }

$publishDir = "$root\artifacts\publish"
$releaseDir = "$root\artifacts\Releases"

# --- 1) Derle (Rust motoru dahil) ve yayinla ---
dotnet build -c Release -r win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz." }

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz." }

# --- 2) Rust motoru pakete girdi mi? ---
if (-not (Test-Path "$publishDir\runa_engine.dll")) {
    throw "runa_engine.dll publish klasorunde yok! Rust motoru pakete girmemis, yayin durduruldu."
}

# --- 3) Onceki surumleri indir (delta guncelleme icin; ilk yayinda yoksa sorun degil) ---
if (-not $SkipUpload -and $repo -notmatch 'KULLANICI_ADI') {
    try { vpk download github --repoUrl $repo --outputDir $releaseDir } catch { Write-Warning "Onceki surumler indirilemedi (ilk yayin olabilir): $_" }
}

# --- 4) Paketle ---
vpk pack --packId RunaVoice --packTitle "Runa Voice" --packVersion $version `
    --packDir $publishDir --mainExe RunaVoice.WinUI.exe --outputDir $releaseDir --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw "vpk pack basarisiz." }

# --- 5) GitHub'a yukle ---
if ($SkipUpload) {
    Write-Host "Yerel paket hazir: $releaseDir" -ForegroundColor Green
    return
}

$token = (gh auth token)
if (-not $token) { throw "GitHub girisi yok: gh auth login" }

vpk upload github --repoUrl $repo --outputDir $releaseDir --publish `
    --releaseName "$version" --tag "v$version" --token $token
if ($LASTEXITCODE -ne 0) { throw "vpk upload basarisiz." }

Write-Host "Yayinlandi: $repo/releases/tag/v$version" -ForegroundColor Green
