@echo off
rem Run on the SERVER (laptop) as administrator, once.
rem Allows client PCs to connect to the server over HTTPS (port 5001) and to ping it.
rem Port 5000 (HTTP) is only used locally on the server itself and needs no rule.
netsh advfirewall firewall add rule name="Club Server (HTTPS 5001)" dir=in action=allow protocol=TCP localport=5001
netsh advfirewall firewall add rule name="Club Server (ping)" dir=in action=allow protocol=icmpv4:8,any
pause
