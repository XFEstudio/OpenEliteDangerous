@echo off
chcp 65001 >nul
dotnet run --project "%~dp0Client\XFEEliteDangerous.Bootstrapper\XFEEliteDangerous.Bootstrapper.csproj" -c Release -- --status
if errorlevel 1 pause