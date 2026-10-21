@echo off
pwsh -NoProfile -File "%~dp0scripts\Stop-ManagementServer.ps1"
if errorlevel 1 pause
