$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 dotnet build src/AppUtilDev/AppUtilDev.csproj -c Release
 if($LASTEXITCODE -ne 0) { throw 'La compilación ha fallado.' }
 $exe=Join-Path $PSScriptRoot 'src/AppUtilDev/bin/Release/net8.0-windows/AppUtilDev.exe'
 $run=Start-Process -FilePath $exe -ArgumentList '--portal-test' -WindowStyle Hidden -Wait -PassThru
 Get-Content artifacts/portal-test.json
 if($run.ExitCode -ne 0) { throw 'Hay pruebas fallidas.' }
} finally { Pop-Location }
