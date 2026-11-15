@echo off
rem Run on the SERVER (laptop) by double click.
rem Builds the agent and the shell (lock screen) into publish\ClubAgent together with .NET,
rem so nothing else has to be installed on the client PC.
cd /d "%~dp0.."
dotnet publish src\Club.Agent -c Release -r win-x64 --self-contained true -o publish\ClubAgent
if errorlevel 1 goto :error
dotnet publish src\Club.Shell -c Release -r win-x64 --self-contained true -o publish\ClubAgent
if errorlevel 1 goto :error
copy /Y scripts\install-agent.cmd publish\ClubAgent\ >nul

rem Put the server certificate thumbprint into the agent settings (certificate pinning).
rem The file appears next to the server after its first start.
set THUMB_FILE=src\Club.Server\server-cert.thumbprint.txt
if exist publish\ClubServer\server-cert.thumbprint.txt set THUMB_FILE=publish\ClubServer\server-cert.thumbprint.txt
if exist "%THUMB_FILE%" (
  powershell -NoProfile -Command "$t=(Get-Content '%THUMB_FILE%').Trim(); $p='publish\ClubAgent\appsettings.json'; $j=Get-Content $p -Raw | ConvertFrom-Json; $j.Agent.ServerCertThumbprint=$t; $j | ConvertTo-Json -Depth 5 | Set-Content $p -Encoding UTF8"
  echo Server certificate thumbprint added to the agent settings.
) else (
  echo WARNING: server certificate thumbprint not found. Start the server once, then run this script again.
)
copy /Y scripts\uninstall-agent.cmd publish\ClubAgent\ >nul
echo.
echo Done. Agent folder: %CD%\publish\ClubAgent
echo Copy it to C:\ClubAgent on the client PC.
explorer publish\ClubAgent
pause
exit /b 0

:error
echo.
echo BUILD FAILED. Copy the text above and send it.
pause
exit /b 1
