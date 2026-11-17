; ---------------------------------------------------------------------------
; CSI Exchange Feed - Inno Setup installer
; Standalone Windows service that streams exchange trades (Bybit, Binance) into
; Sierra Chart .scid files with volume and bid/ask side. Separate from the
; YieldDataLogger installer on purpose: it installs, updates and uninstalls on its
; own and never stops or touches the YieldDataLogger Agent.
; ---------------------------------------------------------------------------

#define AppName      "CSI Exchange Feed"
#define AppVersion   "1.3"
#define AppPublisher "cdsmart-arch"
#define AppURL       "https://github.com/cdsmart-arch/YieldDataLogger"
#define ServiceName  "YieldDataLogger.ExchangeFeed"
#define FeedExe      "YieldDataLogger.ExchangeFeed.exe"

[Setup]
AppId                    = {{C500C083-74D9-47FF-BCCC-17A945556902}
AppName                  = {#AppName}
AppVersion               = {#AppVersion}
AppPublisher             = {#AppPublisher}
AppPublisherURL          = {#AppURL}
AppSupportURL            = {#AppURL}
DefaultDirName           = {autopf}\CSI Exchange Feed
DefaultGroupName         = CSI Exchange Feed
DisableProgramGroupPage  = yes
PrivilegesRequired       = admin
OutputDir                = ..\dist\installer
OutputBaseFilename       = CSI-ExchangeFeed-Setup-{#AppVersion}
SetupIconFile            = ..\src\YieldDataLogger.Manager\appicon.ico
WizardStyle              = modern
Compression              = lzma2/ultra64
SolidCompression         = yes
MinVersion               = 10.0
; The service is stopped in PrepareToInstall; Restart Manager hangs on running services.
CloseApplications        = no
UninstallDisplayName     = {#AppName}
Uninstallable            = yes
CreateUninstallRegKey    = yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; config.json / status.json / state live here; Users need Modify to edit the config.
[Dirs]
Name: "{commonappdata}\YieldDataLogger";              Permissions: users-modify
Name: "{commonappdata}\YieldDataLogger\ExchangeFeed"; Permissions: users-modify

[Files]
Source: "..\dist\ExchangeFeed\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Edit Exchange Feed settings"; Filename: "notepad.exe"; Parameters: """{commonappdata}\YieldDataLogger\ExchangeFeed\config.json"""
Name: "{group}\Exchange Feed status";        Filename: "notepad.exe"; Parameters: """{commonappdata}\YieldDataLogger\ExchangeFeed\status.json"""
Name: "{group}\Uninstall CSI Exchange Feed"; Filename: "{uninstallexe}"

[Run]
; Re-create the service on upgrade (it was stopped in PrepareToInstall).
Filename: "sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated; Check: ServiceExists
Filename: "ping.exe"; Parameters: "127.0.0.1 -n 3 -w 1000"; Flags: runhidden waituntilterminated; Check: ServiceExists
Filename: "sc.exe"; \
  Parameters: "create ""{#ServiceName}"" binPath= """"""{app}\{#FeedExe}"""""" start= auto DisplayName= ""CSI Exchange Feed"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering Exchange Feed service..."
Filename: "sc.exe"; \
  Parameters: "description ""{#ServiceName}"" ""Streams exchange trades (Bybit, Binance) into Sierra Chart .scid files with volume and bid/ask side."""; \
  Flags: runhidden waituntilterminated
; Restart automatically after a crash (5s / 10s / 30s).
Filename: "sc.exe"; Parameters: "failure ""{#ServiceName}"" reset= 86400 actions= restart/5000/restart/10000/restart/30000"; \
  Flags: runhidden waituntilterminated
; Let ordinary users start/stop it (same SDDL as the Agent service).
Filename: "sc.exe"; \
  Parameters: "sdset ""{#ServiceName}"" ""D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;SU)(A;;RPWPCR;;;BU)"""; \
  Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "start ""{#ServiceName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting Exchange Feed service..."

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop ""{#ServiceName}""";   Flags: runhidden waituntilterminated; RunOnceId: "StopFeed"
Filename: "sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFeed"

[Code]
var
  SierraPage: TInputDirWizardPage;

function ConfigPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\YieldDataLogger\ExchangeFeed\config.json');
end;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'query "{#ServiceName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// Stop the running service and wait for its process to exit before files are copied.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, i: Integer;
begin
  Result := '';
  if ServiceExists() then
  begin
    Exec('sc.exe', 'stop "{#ServiceName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for i := 1 to 30 do
    begin
      Exec(ExpandConstant('{cmd}'),
        '/c tasklist /fi "imagename eq {#FeedExe}" /nh | find /i "{#FeedExe}"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        Break;
      Sleep(1000);
    end;
    Exec('taskkill.exe', '/f /im "{#FeedExe}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// First install only: ask where Sierra Chart's Data folder is. Upgrades keep config.json.
procedure InitializeWizard;
begin
  SierraPage := CreateInputDirPage(wpSelectDir,
    'Sierra Chart Data folder',
    'Where should the .scid files be written?',
    'Pick the Data folder of the Sierra Chart install that should receive the exchange feed ' +
    '(for example C:\SierraChart\Data). Symbols appear in Sierra as _XAUUSDT_BYBIT, _XAUUSDT_BINANCE and _COPPERUSDT_BINANCE. ' +
    'You can change this later via Start menu > CSI Exchange Feed > Edit Exchange Feed settings.',
    False, '');
  SierraPage.Add('');
  SierraPage.Values[0] := 'C:\SierraChart\Data';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SierraPage.ID) and FileExists(ConfigPath());
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = SierraPage.ID) and not DirExists(SierraPage.Values[0]) then
    Result := MsgBox('The folder ' + SierraPage.Values[0] + ' does not exist yet.' + #13#10 +
      'The feed will wait until it does. Continue anyway?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Folder: String;
begin
  if (CurStep = ssPostInstall) and not FileExists(ConfigPath()) then
  begin
    Folder := SierraPage.Values[0];
    StringChangeEx(Folder, '\', '\\', True);
    if not SaveStringToFile(ConfigPath(),
      '{' + #13#10 +
      '  "sierraDataFolder": "' + Folder + '",' + #13#10 +
      '  "bybit": {' + #13#10 +
      '    "enabled": true,' + #13#10 +
      '    "symbols": [ "XAUUSDT" ],' + #13#10 +
      '    "historyDays": 90,' + #13#10 +
      '    "volumeMultiplier": 0' + #13#10 +
      '  },' + #13#10 +
      '  "binance": {' + #13#10 +
      '    "enabled": true,' + #13#10 +
      '    "symbols": [ "XAUUSDT", "COPPERUSDT" ],' + #13#10 +
      '    "historyDays": 30,' + #13#10 +
      '    "historyDaysBySymbol": { "COPPERUSDT": 90 },' + #13#10 +
      '    "volumeMultiplier": 0' + #13#10 +
      '  }' + #13#10 +
      '}' + #13#10, False) then
      MsgBox('Could not write ' + ConfigPath() + '. The service will create a default one.', mbError, MB_OK);
  end;
end;
