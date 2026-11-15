@echo off
rem Run on the SERVER (admin PC) by double click.
rem Builds the server into publish\ClubServer together with .NET,
rem so it can run without Visual Studio or an installed .NET runtime.
cd /d "%~dp0.."
dotnet publish src\Club.Server -c Release -r win-x64 --self-contained true -o publish\ClubServer
if errorlevel 1 goto :error
echo.
echo Done. Server folder: %CD%\publish\ClubServer
echo Start it with Club.Server.exe from that folder.
explorer publish\ClubServer
pause
exit /b 0

:error
echo.
echo BUILD FAILED. Copy the text above and send it.
pause
exit /b 1
