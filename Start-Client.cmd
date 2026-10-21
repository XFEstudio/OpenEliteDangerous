@echo off
chcp 65001 >nul
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Dev.ps1" -Role Client %*
if errorlevel 1 pause
