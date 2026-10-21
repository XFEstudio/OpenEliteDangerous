@echo off
pwsh -NoProfile -File "%~dp0scripts\Start-Admin.ps1"
if errorlevel 1 pause
