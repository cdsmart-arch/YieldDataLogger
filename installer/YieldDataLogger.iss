; ---------------------------------------------------------------------------
; YieldDataLogger – Inno Setup installer script
; Produces a single self-contained Setup.exe that non-technical users can
; double-click.  No .NET runtime required (both exes are self-contained).
; ---------------------------------------------------------------------------

#define AppName      "YieldDataLogger"
#define AppVersion   "1.0"
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
Filename: "sc.exe"; Parameters: "stop ""{#ServiceName}""";  Flags: runhidden waituntilterminated; StatusMsg: "Stopping existing service..."; Check: ServiceExists
Filename: "sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Removing old service..."; Check: ServiceExists

; Register the new service.
Filename: "sc.exe"; \
  Parameters: "create ""{#ServiceName}"" binPath= """"""{app}\Agent\{#AgentExe}"""""" start= auto DisplayName= ""YieldDataLogger Agent"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Registering background service..."; \
  Components: agent

; Set a friendly description (shows in services.msc).
Filename: "sc.exe"; \
  Parameters: "description ""{#ServiceName}"" ""Connects to YieldDataLogger Azure hub and writes live price ticks to local files."""; \
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
{ Returns True if the YieldDataLogger.Agent service already exists so the
  [Run] stop/delete steps are only executed on upgrades, not fresh installs. }
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'query "{#ServiceName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;
