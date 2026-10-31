@echo off
rem Double-click to start the graphical NorthCommander setup (setup.ps1). No administrator rights needed.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0setup.ps1"
