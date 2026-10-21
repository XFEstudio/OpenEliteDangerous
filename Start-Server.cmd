@echo off
pwsh -NoProfile -File "%~dp0scripts\Start-ManagementServer.ps1"
if errorlevel 1 pause
