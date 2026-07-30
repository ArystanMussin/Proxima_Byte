@echo off
echo ==============================================
echo  PGW - simulated Modbus TCP + OPC UA servers
echo ==============================================
echo Modbus:  127.0.0.1:15020
echo OPC UA:  opc.tcp://127.0.0.1:4840/pgw/simulator
echo.
echo Keep this window open. Start run-gateway.bat in a SECOND window.
echo Press Ctrl+C to stop.
echo.
PGW.Host.exe simulate
pause
