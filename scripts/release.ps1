<#
  Runa Voice - surum paketleme ve GitHub'a yayinlama.

  Kullanim (proje klasorunden):
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1 -SkipUpload   # yalnizca yerelde paketle

  Surum numarasi RunaVoice.WinUI.csproj icindeki <Version> degerinden okunur;
  yayin oncesi o degeri artirin. Repo adresi Services\UpdateService.cs icindeki RepoUrl'den okunur.
  Gereken araclar:
    dotnet, vpk (dotnet tool install -g vpk),
    Inno Setup 6 (winget install JRSoftware.InnoSetup),
    gh (yukleme icin; winget install GitHub.cli ve gh auth login).

  Kullanicilara verilecek dosya: RunaVoice-Setup.exe (konum secme + masaustu kisayolu sihirbazi).
  Velopack'in kendi Setup.exe/Portable.zip dosyalari GitHub yayininda tutulmaz (karisiklik olmasin);
  guncellemeler icin .nupkg ve releases.win.json yeterlidir.
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
$repo = $Matches[1].TrimEnd('/')
if ($repo -match '\.git$') { throw "RepoUrl'nin sonunda .git olmamali: $repo" }
if ($repo -match 'KULLANICI_ADI') {
    if ($SkipUpload) { Write-Warning "RepoUrl henuz ayarlanmamis (yalnizca yerel paketleme yapilacak)." }
    else { throw "Services\UpdateService.cs icindeki RepoUrl'i kendi GitHub adresinizle degistirin." }
}
$repoSlug = $repo -replace '^https://github\.com/', ''

Write-Host "== Surum $version | Repo $repo ==" -ForegroundColor Cyan

# --- Araclar ---
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw "vpk bulunamadi: dotnet tool install -g vpk" }
if (-not $SkipUpload -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "gh bulunamadi: winget install GitHub.cli ; sonra gh auth login" }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($c in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { $iscc = $c; break }
    }
}
if (-not $iscc) { throw "Inno Setup bulunamadi: winget install JRSoftware.InnoSetup  (kurduktan sonra PowerShell'i yeniden acin)" }

$publishDir = "$root\artifacts\publish"
$releaseDir = "$root\artifacts\Releases"
$installerDir = "$root\artifacts\Installer"

# Onceki yerel paket ciktilarini temizle (eski/denemelik surumler karismasin)
foreach ($d in @($releaseDir, $installerDir)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

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

# --- 4) Velopack paketi (masaustu kisayolunu sihirbaz olusturur, o yuzden burada yalnizca Baslat menusu) ---
vpk pack --packId RunaVoice --packTitle "Runa Voice" --packVersion $version `
    --packDir $publishDir --mainExe RunaVoice.WinUI.exe --outputDir $releaseDir --runtime win-x64 `
    --shortcuts StartMenuRoot
if ($LASTEXITCODE -ne 0) { throw "vpk pack basarisiz." }

# --- 5) Kurulum sihirbazi (konum secme + masaustu kisayolu) ---
& $iscc "/DAppVersion=$version" "$root\installer\RunaVoice.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi basarisiz." }
$installer = "$installerDir\RunaVoice-Setup.exe"
if (-not (Test-Path $installer)) { throw "Kurulum sihirbazi olusmadi: $installer" }

if ($SkipUpload) {
    Write-Host "Yerel paket hazir. Kurulum dosyasi: $installer" -ForegroundColor Green
    return
}

# --- 6) GitHub'a yukle ---
$token = (gh auth token)
if (-not $token) { throw "GitHub girisi yok: gh auth login" }

vpk upload github --repoUrl $repo --outputDir $releaseDir --publish `
    --releaseName "$version" --tag "v$version" --token $token
if ($LASTEXITCODE -ne 0) { throw "vpk upload basarisiz." }

gh release upload "v$version" $installer --repo $repoSlug --clobber
if ($LASTEXITCODE -ne 0) { throw "Kurulum sihirbazi GitHub'a yuklenemedi." }

# Karisiklik olmasin: Velopack'in kendi Setup.exe ve Portable.zip dosyalarini yayindan kaldir.
foreach ($asset in @("RunaVoice-win-Setup.exe", "RunaVoice-win-Portable.zip")) {
    gh release delete-asset "v$version" $asset --repo $repoSlug --yes 2>$null
}

Write-Host "Yayinlandi: $repo/releases/tag/v$version" -ForegroundColor Green
Write-Host "Kullanicilara verilecek dosya: RunaVoice-Setup.exe" -ForegroundColor Green
