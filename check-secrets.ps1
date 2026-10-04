param([switch]$Staged)
$ErrorActionPreference = 'Stop'
# Report only filenames and line numbers, never credential values.
$patterns = @(
    'sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}',
    'gh[pousr]_[A-Za-z0-9]{30,}',
    'github_pat_[A-Za-z0-9_]{30,}',
    'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)["'']?(?:access_token|refresh_token|OPENAI_API_KEY|CHATGPT_TOKEN)["'']?\s*[:=]\s*["''][^"'']{12,}["'']'
)
$extensions = @('.cs','.csproj','.xaml','.manifest','.json','.md','.ps1','.yml','.yaml','.toml','.config','.xml','.txt','.env','.example')
$hits = @()
if ($Staged) {
    Push-Location $PSScriptRoot
    try {
        $stagedFiles = @(git ls-files)
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo leer el índice Git.' }
        foreach ($relativePath in $stagedFiles) {
            # The root single-file distribution is explicitly published by build_exe.bat.
            if ($relativePath -eq 'AppUtilDev.exe') {
                $binary = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $relativePath))
                if ($binary.Length -lt 2 -or $binary[0] -ne 77 -or $binary[1] -ne 90 -or $binary.Length -ge 100MB) { throw 'Distribución EXE inválida o demasiado grande para GitHub.' }
                continue
            }
            if ($relativePath -match '(?i)(^|/)(bin|obj|dist|artifacts|\.codex|ClaudeBrowser|data|logs|screenshots)(/|$)|(^|/)(auth|settings|secrets)\.json$|(^|/)\.env(\.|$)|\.(exe|dll|pdb|db|sqlite\w*|log|pfx|p12|pem|key|dmp|etl|har|zip|7z|csv)$') {
                $hits += $relativePath
            }
            $content = (git show ":$relativePath") -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'No se pudo leer un archivo del índice Git.' }
            foreach ($pattern in $patterns) {
                if ([regex]::IsMatch($content, $pattern)) { $hits += $relativePath; break }
            }
        }
        if ($hits.Count) {
            $hits | Sort-Object -Unique | Write-Output
            throw 'No publiques: posibles secretos o archivos privados en el índice Git.'
        }
        Write-Output "Índice Git: $($stagedFiles.Count) archivos revisados, sin coincidencias ni archivos privados."
    } finally { Pop-Location }
    exit 0
}
$files = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Force | Where-Object {
    $_.FullName -notmatch '[\\/](?:bin|obj|dist|artifacts|\.git|\.vs)[\\/]' -and
    ($_.Extension -in $extensions -or $_.Name -like '.env*')
})
foreach ($file in $files) {
    $matches = @(Select-String -LiteralPath $file.FullName -Pattern $patterns)
    foreach ($match in $matches) { $hits += "$($file.FullName):$($match.LineNumber)" }
    if ($file.Name -in @('auth.json','secrets.json') -or $file.FullName -match '[\\/]\.codex[\\/]') { $hits += $file.FullName }
}
if ($hits.Count) {
    $hits | Sort-Object -Unique | Write-Output
    throw 'Posibles credenciales detectadas. No publiques estos archivos.'
}
Write-Output "Comprobación de secretos: $($files.Count) archivos revisados, sin coincidencias."

