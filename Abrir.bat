@echo off
cd /d "%~dp0"
if not exist "dist\AppUtilDev.exe" (
 powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
 if errorlevel 1 exit /b 1
)
start "" "%~dp0dist\AppUtilDev.exe" --show
