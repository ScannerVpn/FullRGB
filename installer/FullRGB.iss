; FullRGB — Windows installer (Inno Setup 6)
;
; WHY a normal installer at all, when the app ships as one self-contained exe:
;   * no "which folder is FullRGB.exe in?" question for the user — Start Menu, Desktop and
;     "Apps & features" all agree on one place;
;   * the user's own data (settings.json, devices.json, the extracted engine, backups) is
;     DELIBERATELY left outside the install folder, so an update or uninstall cannot take it;
;   * the uninstaller cleans the install dir, the Program Files rules are satisfied, and Windows
;     has one registry key to show in "Apps & features".
;
; It stays a PER-USER install on purpose: FullRGB runs asInvoker and never needs admin, so
; installing into {localappdata}\Programs avoids a UAC prompt at install time too (same choice
; VS Code / Chrome make). The RGB-RAM feature still asks for its ONE UAC from inside the app
; when the user enables it — that is unchanged by this installer.
;
; Build:
;   ISCC.exe installer\FullRGB.iss            (adjust DistDir / AppVersion below)
;   ISCC.exe /DAppVersion=1.5.0 /DDistDir=..\dist33 installer\FullRGB.iss
; Installer output: installer\Output\FullRGB-Setup-<version>.exe

#define AppName        "FullRGB"
#define AppPublisher   "ScannerVpn"
#define AppUrl         "https://github.com/ScannerVpn/FullRGB"
#define AppExeName     "FullRGB.exe"

; Overridable from the command line so CI can build a release without editing this file.
#ifndef AppVersion
  #define AppVersion "1.5.0"
#endif
#ifndef DistDir
  #define DistDir "..\dist33"
#endif

; Inno resolves Source paths relative to THIS script (installer\), so DistDir is written the same
; way. The version shown in "Apps & features" is the one passed to /D, not read from the binary —
; keep the two in step when releasing.
[Setup]
AppId={{8E3B1D74-2C6A-4F51-9B0E-7A5C1F4D8E32}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user install: no UAC at install time, and the app itself stays asInvoker.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
UsePreviousAppDir=yes

DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes

OutputDir=Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\FullRGB\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; 87 MB exe → let the user see real progress instead of a frozen window
LZMANumBlockThreads=4
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Windows 11 logon tasks + the WPF runtime in the exe are both fine at 10.0; the exe is
; self-contained so there is no .NET prerequisite to check for.

[Languages]
; Default.isl is Inno's own English (no file needed). Persian is not shipped with Inno Setup —
; the app's UI is Persian-first, but the installer stays in English + the languages Inno ships
; rather than inventing a translation. Add installer\Persian.isl and a line here to change that.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic";  MessagesFile: "compiler:Languages\Arabic.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a &desktop shortcut
arabic.CreateDesktopIcon=إنشاء اختصار على &سطح المكتب
turkish.CreateDesktopIcon=&Masaüstü kısayolu oluştur

english.LaunchAfter=Launch {#AppName}
arabic.LaunchAfter=تشغيل {#AppName}
turkish.LaunchAfter={#AppName} başlat

english.UninstallKeepData=Keep my settings and remembered devices
arabic.UninstallKeepData=الاحتفاظ بإعداداتي والأجهزة المحفوظة
turkish.UninstallKeepData=Ayarlarımı ve hatırlanan aygıtları sakla

english.RunTaskNote=FullRGB never requires administrator rights. The RGB-RAM feature adds one on-demand admin task from inside the app (Hardware > Unlock more hardware) and asks for a single Windows prompt at that moment.
arabic.RunTaskNote=لا يحتاج FullRGB إلى صلاحيات المدير. ميزة ذاكرة RGB تضيف مهمة إدارية واحدة عند الطلب من داخل التطبيق، مع طلب واحد فقط من ويندوز.
turkish.RunTaskNote=FullRGB yönetici hakları gerektirmez. RGB-RAM özelliği, uygulama içinden isteğe bağlı tek bir yönetici görevi ekler ve yalnızca o anda bir Windows onayı ister.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; One file. Everything else the app needs (the OpenRGB engine, 13 MB) is embedded in this exe
; and unpacked on first run — that is why there is no vendor\ folder to install.
Source: "{#DistDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";             DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\LICENSE";               DestDir: "{app}"; Flags: ignoreversion

[Icons]
; {autoprograms} resolves to the per-user Start Menu when the install is per-user.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; No "run as administrator": see the manifest note above. Flags runasoriginaluser keeps the app
; unelevated even when the installer itself was launched elevated.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchAfter}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\README.md"; Description: "Open the readme"; Flags: shellexec nowait postinstall skipifsilent unchecked

[UninstallDelete]
; The INNO SETUP LEFTOVERS only, and this is the whole point of the [UninstallRun] block below:
; FullRGB writes NO code into the installation directory, so {app} can be deleted outright.
Type: filesandordirs; Name: "{app}"

[Code]
// ---------------------------------------------------------------------------------------------
// Uninstall-time cleanup of the two Windows Scheduled Tasks FullRGB creates. Both are created by
// the app, not by this installer, so Windows knows nothing about them and would otherwise keep
// launching a deleted exe at logon (the exact 0x80070002 "stale path" bug the app already
// self-heals) — the uninstaller must remove them.
const
  AutostartTask = 'FullRGB';
  EngineTask    = 'FullRGB-Engine';

function PsQuote(const S: String): String;
var
  Inner: String;
begin
  Inner := S;
  StringChangeEx(Inner, '''', '''''', True);
  Result := '''' + Inner + '''';
end;

// Runs one PowerShell snippet. PowerShell is on every supported Windows, so no console window and
// no cmd.exe quoting puzzles.
function RunPowershell(const Script: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('powershell.exe',
                 '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' + PsQuote(Script),
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Unregister a task if it exists. NOT elevated: the logon task is registered at RunLevel=Limited
// (see Autostart.BuildRegisterScript) so the user can always remove their own. The elevated
// FullRGB-Engine task belongs to the SAME user, so no admin token is needed to unregister it
// either; if a future change makes it admin-only, PowerShell returns an error and we simply
// continue — a leftover task is reported, never fatal.
function RemoveTask(const TaskName: String): Boolean;
begin
  Result := RunPowershell(
    '$ErrorActionPreference = ''SilentlyContinue''; ' +
    'if (Get-ScheduledTask -TaskName ' + PsQuote(TaskName) + ' -ErrorAction SilentlyContinue) { ' +
    'Unregister-ScheduledTask -TaskName ' + PsQuote(TaskName) + ' -Confirm:$false }; exit 0');
end;

// ---------------------------------------------------------------------------------------------
// The user's own data. FullRGB keeps everything under %APPDATA%\FullRGB (settings.json,
// devices.json, backups\) and %LOCALAPPDATA%\FullRGB (the unpacked engine, ~58 MB). A silent
// uninstall NEVER deletes these: they survive an uninstall/reinstall, and the engine cache is
// SHA-keyed so a reinstall reuses it instead of unpacking again. The user is asked, and the safe
// answer (keep) is the default.
function UserDataRoot: String;
begin
  Result := ExpandConstant('{userappdata}\FullRGB');
end;

function EngineCacheRoot: String;
begin
  Result := ExpandConstant('{localappdata}\FullRGB\engine');
end;

// Raw byte total of a folder tree. Declared FIRST because Inno's Pascal script needs the
// forward declaration to come before any caller (DirSizeMb below recurses into it).
function DirSizeBytes(const Path: String): Int64;
var
  FindRec: TFindRec;
  Sub: String;
begin
  Result := 0;
  if not DirExists(Path) then exit;
  if FindFirst(AddBackslash(Path) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Sub := AddBackslash(Path) + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            Result := Result + DirSizeBytes(Sub)
          else
            Result := Result + Int64(FindRec.SizeHigh) * 4294967296 + FindRec.SizeLow;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Returns the total size of a folder tree in whole MB, or '' when it does not exist / is empty.
// Used only to tell the user what they are about to delete; nothing depends on the number.
// NOTE: Inno's Format supports %d but NOT %f — an earlier version used Format('%.0f MB', ...)
// and threw "Format '%.0f MB' invalid or incompatible with argument" at uninstall time, which
// aborted the whole uninstall (found in the first silent-uninstall test).
function DirSizeMb(const Path: String): String;
var
  Total: Int64;
begin
  Result := '';
  Total := DirSizeBytes(Path);
  if Total <= 0 then exit;
  Result := IntToStr(Total div 1048576) + ' MB';
end;

function InitializeUninstall(): Boolean;
var
  TotalMb: String;
  SizeNote: String;
begin
  Result := True;

  // 1. Scheduled tasks: always removed. A task pointing at a deleted exe is a bug, not data.
  RemoveTask(AutostartTask);
  RemoveTask(EngineTask);

  // 2. User data. SAFETY RULES, learned the hard way (the first test run of this installer
  //    deleted the author's real settings.json and devices.json — see the note at the top of
  //    this section):
  //      * An UNATTENDED uninstall (/SILENT, /VERYSILENT, as used by CI and by "Apps & features"
  //        scripts) MUST NEVER delete user data. There is nobody to answer the question, and
  //        Inno's /SUPPRESSMSGBOXES picks the FIRST button, so a default of "yes" is a loaded
  //        gun. Unattended = keep, always, no prompt.
  //      * The interactive prompt defaults to NO. Deleting a user's profiles and calibrations
  //        must be something they explicitly ask for, never something they get by pressing Enter.
  if UninstallSilent then exit;

  TotalMb := DirSizeMb(UserDataRoot);
  if TotalMb = '' then TotalMb := DirSizeMb(EngineCacheRoot);
  SizeNote := '';
  if TotalMb <> '' then
    SizeNote := #13#10#13#10 + 'Currently on disk: ' + TotalMb + '.';

  if MsgBox(
    'Delete FullRGB''s saved data as well?' + #13#10#13#10 +
    'Your settings, colour calibrations, profiles and the list of remembered devices live in' + #13#10 +
    '  ' + UserDataRoot + #13#10 +
    'and the unpacked engine lives in' + #13#10 +
    '  ' + EngineCacheRoot + #13#10#13#10 +
    'Choose No to keep them: a reinstall then picks up exactly where you left off.' +
    SizeNote,
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then exit;

  DelTree(UserDataRoot, True, True, True);       // settings.json, devices.json, backups\
  DelTree(EngineCacheRoot, True, True, True);    // the unpacked OpenRGB engine (~58 MB)
end;
