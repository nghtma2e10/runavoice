; Runa Voice kurulum sihirbazi (Inno Setup 6).
; Velopack'in Setup.exe'sini sarmalar: kullanici kurulum konumunu secer, masaustu kisayolunu
; isteye bagli ekler; asil kurulum ve otomatik guncelleme Velopack tarafindan yapilir.
; release.ps1 bu dosyayi derler:  iscc /DAppVersion=1.0.1 installer\RunaVoice.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{8E6F2D0A-5B7C-4C11-9A3E-1F4D7B2C6E90}
AppName=Runa Voice
AppVersion={#AppVersion}
AppPublisher=Runa Voice
; Uygulamanin ayarlari %LocalAppData%\RunaVoice klasorunde tutulur; kurulum o klasorun disina yapilir
; (Velopack kaldirirken kurulum klasorunu tamamen siler).
DefaultDirName={localappdata}\Programs\RunaVoice
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\Installer
OutputBaseFilename=RunaVoice-Setup
SetupIconFile=..\Assets\AppIcon.ico
WizardStyle=modern
; Kaldirma ve guncelleme Velopack'e ait; Inno kendi kaldirma kaydini olusturmasin.
Uninstallable=no
CreateUninstallRegKey=no
; Ic Setup.exe zaten sikistirilmis.
Compression=none
SolidCompression=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol ekle"; GroupDescription: "Ek görevler:"

[Files]
Source: "..\artifacts\Releases\RunaVoice-win-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autodesktop}\Runa Voice"; Filename: "{app}\current\RunaVoice.WinUI.exe"; WorkingDir: "{app}\current"; Tasks: desktopicon

[Code]
function DirHasForeignFiles(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Dir) then Exit;
  if FileExists(Dir + '\Update.exe') then Exit; { onceki Runa Voice kurulumu }
  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsWritableDir(const Dir: String): Boolean;
var
  Probe: String;
begin
  Result := ForceDirectories(Dir);
  if Result then
  begin
    Probe := Dir + '\.rv_yazma_testi';
    Result := SaveStringToFile(Probe, 'x', False);
    if Result then DeleteFile(Probe);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if DirHasForeignFiles(WizardDirValue) then
    begin
      MsgBox('Seçtiğiniz klasör boş değil. Kurulum o klasördeki dosyaları silebileceği için ' +
             'lütfen boş bir klasör veya yeni bir klasör adı seçin (örn. ...\RunaVoice).', mbError, MB_OK);
      Result := False;
    end
    else if not IsWritableDir(WizardDirValue) then
    begin
      MsgBox('Bu klasöre yazılamıyor. Lütfen yazma iznine sahip bir klasör seçin ' +
             '(örn. Program Files yerine belgeleriniz veya D: sürücünüz).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Runa Voice kuruluyor...';
    if (not Exec(ExpandConstant('{tmp}\RunaVoice-win-Setup.exe'),
                 '--installto "' + WizardDirValue + '"', '', SW_SHOW,
                 ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    begin
      MsgBox('Kurulum tamamlanamadı (hata kodu ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
    end;
  end;
end;
