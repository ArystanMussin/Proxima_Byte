@echo off
set PGW_CONFIG=%~dp0project.yaml
echo ==============================================
echo  PGW gateway - demo config (points at simulator)
echo ==============================================
echo Make sure run-simulate.bat is already running in another window!
echo Dashboard: http://localhost:8420/
echo.
start http://localhost:8420/
PGW.Host.exe
pause
