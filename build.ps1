$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 dotnet publish src/AppUtilDev/AppUtilDev.csproj -c Release -r win-x64 --self-contained true -o dist -p:PublishSingleFile=false
 if($LASTEXITCODE -ne 0) { throw 'La compilación ha fallado.' }
} finally { Pop-Location }
