@echo off
rem Runs the automatic tests (tests\Club.Tests). The server does not have to be running.
cd /d "%~dp0.."
dotnet test tests\Club.Tests --logger "console;verbosity=normal"
echo.
if errorlevel 1 (echo SOME TESTS FAILED) else (echo ALL TESTS PASSED)
pause
