@echo off
pwsh -NoProfile -File "%~dp0scripts\Start-OnlineGame.ps1" %*
if errorlevel 1 pause
