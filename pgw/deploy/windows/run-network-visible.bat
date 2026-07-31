@echo off
rem Optional: only needed if you want the dashboard reachable from OTHER computers on the
rem network. By default PGW listens on 127.0.0.1 (this machine only) — see README.txt.
set PGW_API_URL=http://0.0.0.0:8420
echo Dashboard will be reachable at http://<this-computer-IP>:8420/ from other machines on the network.
PGW.Host.exe
pause
