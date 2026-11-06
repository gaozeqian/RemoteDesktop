@echo off
rem Runs the PowerShell script beside this file without depending on the machine's execution policy. Windows 11
rem clients default to Restricted, which blocks even a local, unsigned script; the server package supports them.
setlocal
set "SCRIPT=%~dp0Set-ServerConfiguration.ps1"
where pwsh >nul 2>nul
if not errorlevel 1 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
)
exit /b %errorlevel%
