@echo off
cd /d "%~dp0"
if not exist "AppUtilDev.exe" (
 call "%~dp0build_exe.bat"
 if errorlevel 1 exit /b 1
)
start "" "%~dp0AppUtilDev.exe" --show
