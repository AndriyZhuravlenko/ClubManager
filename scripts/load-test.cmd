@echo off
rem Load test: many virtual agents connect to the RUNNING server at the same time.
rem Usage: load-test.cmd [agents] [seconds] [heartbeat-interval-seconds] [owner-login]
rem Results are printed and appended to loadtest-results.csv in the project folder.
cd /d "%~dp0.."
set AGENTS=%1
if "%AGENTS%"=="" set /p AGENTS=How many virtual agents (for example 50, 100, 200): 
set DURATION=%2
if "%DURATION%"=="" set DURATION=60
set INTERVAL=%3
if "%INTERVAL%"=="" set INTERVAL=15
set LOGIN=%4
if "%LOGIN%"=="" set /p LOGIN=Owner login to measure commands and remove test PCs afterwards (Enter = skip): 
set EXTRA=
if not "%LOGIN%"=="" set EXTRA=--login %LOGIN%
dotnet run --project tools\Club.LoadTest -c Release -- --agents %AGENTS% --duration %DURATION% --interval %INTERVAL% %EXTRA%
pause
