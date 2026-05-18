; ---------------------------------------------------------------------------
; YieldDataLogger – Inno Setup installer script
; Produces a single self-contained Setup.exe that non-technical users can
; double-click.  No .NET runtime required (both exes are self-contained).
; ---------------------------------------------------------------------------

#define AppName      "YieldDataLogger"
#define AppVersion   "1.1"
#define AppPublisher "cdsmart-arch"
#define AppURL       "https://github.com/cdsmart-arch/YieldDataLogger"
#define ServiceName  "YieldDataLogger.Agent"
#define AgentExe     "YieldDataLogger.Agent.exe"
#define ManagerExe   "YieldDataLogger.Manager.exe"

[Setup]
AppId                    = {{B3F7A2D1-4E8C-4F9A-A2B1-1C3D5E7F9A0B}
AppName                  = {#AppName}
AppVersion               = {#AppVersion}
AppPublisher             = {#AppPublisher}
AppPublisherURL          = {#AppURL}
AppSupportURL            = {#AppURL}
AppUpdatesURL            = {#AppURL}

; Default install directory – can be changed by the user in the wizard.
DefaultDirName           = {autopf}\{#AppName}
DefaultGroupName         = {#AppName}
DisableProgramGroupPage  = yes

; Require admin so we can register the Windows Service.
PrivilegesRequired       = admin

; Output
OutputDir                = ..\dist\installer
OutputBaseFilename       = YieldDataLogger-Setup-{#AppVersion}

; Cosmetics
SetupIconFile            = ..\src\YieldDataLogger.Manager\appicon.ico
WizardStyle              = modern

; Compression
Compression              = lzma2/ultra64
SolidCompression         = yes

; Minimum Windows version: 10
MinVersion               = 10.0

; Uninstaller
UninstallDisplayName     = {#AppName}
UninstallDisplayIcon     = {app}\Manager\{#ManagerExe}
Uninstallable            = yes
CreateUninstallRegKey    = yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full";    Description: "Full installation (Agent + Manager)"
Name: "agent";   Description: "Agent only (no tray dashboard)"

[Components]
Name: "agent";   Description: "YieldDataLogger Agent (background service)"; Types: full agent; Flags: fixed
Name: "manager"; Description: "YieldDataLogger Manager (system tray dashboard)"; Types: full

; ---------------------------------------------------------------------------
; Files
; ---------------------------------------------------------------------------
[Files]
; Agent – all files from the self-contained publish output
Source: "..\dist\Agent\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: agent

; Manager – all files from the self-contained publish output
Source: "..\dist\Manager\*"; DestDir: "{app}\Manager"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: manager

; ---------------------------------------------------------------------------
; Icons (Start Menu)
; ---------------------------------------------------------------------------
[Icons]
Name: "{group}\YieldDataLogger Manager"; Filename: "{app}\Manager\{#ManagerExe}"; Components: manager
Name: "{group}\Uninstall YieldDataLogger"; Filename: "{uninstallexe}"

; ---------------------------------------------------------------------------
; Registry – add Manager to per-user startup so it appears in the tray on login
; ---------------------------------------------------------------------------
[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "YieldDataLogger.Manager"; \
  ValueData: """{app}\Manager\{#ManagerExe}"""; \
  Flags: uninsdeletevalue; Components: manager

; ---------------------------------------------------------------------------
; Run – executes during install (hidden, so the user sees no console flash)
; ---------------------------------------------------------------------------
[Run]
; Stop any existing service before we overwrite the exe (upgrade scenario).
; Stop + delete the existing service on upgrades, then pause briefly so the
; SCM releases the handle before we re-create it.
Filename: "sc.exe";      Parameters: "stop ""{#ServiceName}""";   Flags: runhidden waituntilterminated; StatusMsg: "Stopping existing service...";  Check: ServiceExists
Filename: "sc.exe";      Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Removing old service...";         Check: ServiceExists
Filename: "ping.exe";    Parameters: "127.0.0.1 -n 3 -w 1000";   Flags: runhidden waituntilterminated; StatusMsg: "Waiting for SCM to release...";   Check: ServiceExists

; Register the new service.
Filename: "sc.exe"; \
  Parameters: "create ""{#ServiceName}"" binPath= """"""{app}\Agent\{#AgentExe}"""""" start= auto DisplayName= ""YieldDataLogger Agent"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Registering Agent service..."; \
  Components: agent

; Set a friendly description (shows in services.msc).
Filename: "sc.exe"; \
  Parameters: "description ""{#ServiceName}"" ""Connects to the YieldDataLogger hub and writes live price ticks to local files."""; \
  Flags: runhidden waituntilterminated; Components: agent

; Grant BUILTIN\Users permission to start and stop the service so the Manager
; tray app works without requiring admin rights.
; SDDL breakdown of the last ACE (A;;RPWPCR;;;BU):
;   RP = SERVICE_START, WP = SERVICE_STOP, CR = SERVICE_USER_DEFINED_CONTROL
;   BU = BUILTIN\Users (all local accounts)
Filename: "sc.exe"; \
  Parameters: "sdset ""{#ServiceName}"" ""D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;SU)(A;;RPWPCR;;;BU)"""; \
  Flags: runhidden waituntilterminated; Components: agent

; Start the service.
Filename: "sc.exe"; Parameters: "start ""{#ServiceName}"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Starting YieldDataLogger Agent service..."; \
  Components: agent

; Launch Manager so it appears in the tray immediately (don't wait for next login).
Filename: "{app}\Manager\{#ManagerExe}"; \
  Description: "Launch YieldDataLogger Manager now"; \
  Flags: nowait postinstall skipifsilent; Components: manager

; ---------------------------------------------------------------------------
; UninstallRun – stop and delete the service on uninstall
; ---------------------------------------------------------------------------
[UninstallRun]
Filename: "sc.exe"; Parameters: "stop ""{#ServiceName}""";   Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated

; Kill the Manager tray app so the folder can be deleted cleanly.
Filename: "taskkill.exe"; Parameters: "/f /im ""{#ManagerExe}"""; Flags: runhidden waituntilterminated

; ---------------------------------------------------------------------------
; Pascal script helpers
; ---------------------------------------------------------------------------
[Code]
var
  SecretPage: TInputQueryWizardPage;

// Returns True if the YieldDataLogger.Agent service already exists so the
// stop/delete steps in the Run section are only executed on upgrades.
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'query "{#ServiceName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// Add a custom wizard page after the Components selection that collects the
// shared ingest secret from the user. The secret is not bundled in the installer
// (it stays out of git); each install gets it from the administrator out-of-band.
procedure InitializeWizard;
begin
  SecretPage := CreateInputQueryPage(
    wpSelectComponents,
    'Ingest Secret',
    'Connect this Agent to the YieldDataLogger hub',
    'Paste the ingest secret you received from the YDL administrator. ' +
    'It is a 64-character hex string and authenticates this Agent to the hub at ydl.csindicators.com. ' +
    'You can leave this blank for now and edit %ProgramData%\YieldDataLogger\Agent\appsettings.Production.json later if you do not have it yet.');
  SecretPage.Add('Ingest Secret:', False);
end;

// Light validation: warn if the value doesn't look like the 64-char hex secret
// we expect. Allow blank (so users without a secret yet can still install and
// fill it in later) but require either blank or sensible length.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  S: string;
begin
  Result := True;
  if CurPageID = SecretPage.ID then
  begin
    S := Trim(SecretPage.Values[0]);
    if (Length(S) > 0) and (Length(S) < 16) then
    begin
      MsgBox(
        'That secret looks too short. Expected a 64-character hex string from your administrator.' + #13#10#13#10 +
        'Click OK to go back and re-paste, or clear the field to install without a secret (the Agent will not be able to connect until you add the secret to appsettings.Production.json).',
        mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// After files are copied but before the Run section starts the service, write
// the AuthToken into a Production-environment override. ASP.NET's configuration
// chain (appsettings.json -> appsettings.{Environment}.json) auto-merges this on
// top of the bundled appsettings.json without us needing to touch the bundled file.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: string;
  ConfigContent: string;
  Secret: string;
begin
  if CurStep = ssPostInstall then
  begin
    Secret := Trim(SecretPage.Values[0]);
    if Secret = '' then
      Exit;  // Skip override file; user will create/edit it manually later

    ConfigPath := ExpandConstant('{app}\Agent\appsettings.Production.json');
    ConfigContent :=
      '{' + #13#10 +
      '  "Agent": {' + #13#10 +
      '    "AuthToken": "' + Secret + '"' + #13#10 +
      '  }' + #13#10 +
      '}' + #13#10;
    if not SaveStringToFile(ConfigPath, ConfigContent, False) then
      MsgBox(
        'Could not write the secret to ' + ConfigPath + '.' + #13#10 +
        'You will need to add it manually: open that file in notepad as Administrator and paste the AuthToken under the "Agent" section.',
        mbError, MB_OK);
  end;
end;
