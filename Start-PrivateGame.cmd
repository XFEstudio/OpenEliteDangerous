@echo off
chcp 65001 >nul
pushd "%~dp0"
dotnet run --project "%~dp0Client\XFEEliteDangerous.Bootstrapper\XFEEliteDangerous.Bootstrapper.csproj" -c Release -- --launch %*
set "XFE_LAUNCH_EXIT=%ERRORLEVEL%"
popd
if not "%XFE_LAUNCH_EXIT%"=="0" pause
exit /b %XFE_LAUNCH_EXIT%
