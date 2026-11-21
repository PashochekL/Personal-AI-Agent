@echo off
setlocal
set "damir_runtime=%~dp0.dotnet\tools\pwsh.exe"
if exist "%damir_runtime%" goto run
"%SystemRoot%\System32\where.exe" pwsh >nul 2>&1
if errorlevel 1 goto missing
set "damir_runtime=pwsh"
:run
"%damir_runtime%" -NoLogo -NoProfile -File "%~dp0damir-launcher.ps1" %*
exit /b %errorlevel%
:missing
>&2 echo PowerShell 7 not found. Install it or restore .dotnet\tools\pwsh.exe.
exit /b 1
