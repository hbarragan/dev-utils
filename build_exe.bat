@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\AppUtilDev\AppUtilDev.csproj -c Release -r win-x64 --self-contained true -o .tools\single-exe -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 exit /b 1
copy /y ".tools\single-exe\AppUtilDev.exe" "AppUtilDev.exe" >nul
if errorlevel 1 exit /b 1
echo Ejecutable generado: %~dp0AppUtilDev.exe
exit /b 0
