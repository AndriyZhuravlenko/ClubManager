@echo off
rem Run on the CLIENT PC (virtual machine) as administrator.
rem Stop the service first, otherwise it would restart the shell immediately.
sc.exe stop ClubAgent
timeout /t 3 /nobreak >nul
sc.exe delete ClubAgent
taskkill /im Club.Shell.exe /f
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v ClubShell /f
echo.
echo Done.
pause
