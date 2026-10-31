@echo off
rem Checks what NorthCommander needs on this PC and offers to install missing parts (deps.ps1). No administrator rights needed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deps.ps1" %*
echo.
pause
