@echo off
rem Run on the CLIENT PC (virtual machine) from an ELEVATED command prompt:
rem Start -> type cmd -> right click "Command Prompt" -> "Run as administrator".
rem Registers the agent as a Windows service, starts the user shell (lock screen)
rem and restricts access to the agent folder.
set DIR=%~dp0

rem Check that we really run as administrator (otherwise nothing below will work).
net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: this script must be run as administrator.
  pause
  exit /b 1
)

echo [1/3] Installing the service...
sc.exe create ClubAgent binPath= "%DIR%Club.Agent.exe" start= auto DisplayName= "Club Agent"
sc.exe description ClubAgent "Club Manager agent: user sessions and software access control"
rem Restart the service automatically if it crashes or gets killed.
sc.exe failure ClubAgent reset= 0 actions= restart/5000/restart/5000/restart/5000
sc.exe start ClubAgent

echo [2/3] Registering and starting the shell...
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v ClubShell /t REG_SZ /d "\"%DIR%Club.Shell.exe\"" /f
start "" "%DIR%Club.Shell.exe"

echo [3/3] Setting folder permissions...
if not exist "%DIR%logs" mkdir "%DIR%logs"
rem Permissions are set on FOLDERS only (with inheritance flags), files inherit them:
rem   agent folder: SYSTEM and Administrators - full access, Users - read and execute;
rem   logs folder and appsettings.json (contains the agent key): SYSTEM and Administrators only.
rem S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators, S-1-5-32-545 = Users
rem All on ONE line: this script itself is inside the folder whose permissions change,
rem so cmd must not need to read the file again after this point.
icacls "%DIR%." /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /Q & icacls "%DIR%*" /reset /T /Q & icacls "%DIR%logs" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /Q & icacls "%DIR%appsettings.json" /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" /Q & echo. & echo Done. & pause & exit /b 0
