@echo off
pwsh -NoProfile -File "%~dp0scripts\Start-ManagedGame.ps1" %*
if errorlevel 1 pause
