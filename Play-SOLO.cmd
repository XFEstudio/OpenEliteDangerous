@echo off
chcp 65001 >nul
"%~dp0bootstrapper\XFEEliteDangerous.Bootstrapper.exe" --play-solo %*
set "XFE_SOLO_EXIT=%ERRORLEVEL%"
if not "%XFE_SOLO_EXIT%"=="0" pause
exit /b %XFE_SOLO_EXIT%
