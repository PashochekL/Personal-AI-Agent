#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repoDir = Split-Path -Parent $PSScriptRoot
$fixtureDir = Join-Path ([IO.Path]::GetTempPath()) ('damir-launch-check-' + [Guid]::NewGuid().ToString('N'))
$oldPath = $env:PATH
$oldZone = $env:DAMIR_TIMEZONE
$oldLog = $env:DAMIR_TEST_LOG
$oldFail = $env:DAMIR_TEST_FAIL
$oldWorkspaceLog = $env:DAMIR_TEST_WORKSPACE_LOG
$oldSession = $env:DAMIR_SESSION_ID
$oldMemoryProject = $env:DAMIR_MEMORY_PROJECT
$oldMemoryEnabled = $env:DAMIR_MEMORY_ENABLED
$oldGithubToken = $env:DAMIR_GITHUB_TOKEN
$pwsh = (Get-Command pwsh -CommandType Application).Source | Select-Object -First 1
try {
    $fakeBin = Join-Path $fixtureDir 'bin'
    $configDir = Join-Path $fixtureDir 'config'
    [IO.Directory]::CreateDirectory($fakeBin) | Out-Null
    [IO.Directory]::CreateDirectory($configDir) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoDir 'damir-launcher.ps1') -Destination $fixtureDir
    Copy-Item -LiteralPath (Join-Path $repoDir 'damir.cmd') -Destination $fixtureDir
    Copy-Item -LiteralPath (Join-Path $repoDir 'damir') -Destination $fixtureDir
    [IO.File]::WriteAllText((Join-Path $configDir 'litellm.yaml'), 'model_list: []')
    $env:DAMIR_TEST_LOG = Join-Path $fixtureDir 'calls.log'
    $env:DAMIR_TEST_WORKSPACE_LOG = Join-Path $fixtureDir 'workspace.log'
    $env:DAMIR_SESSION_ID = ''
    $env:DAMIR_MEMORY_PROJECT = ''
    $env:DAMIR_MEMORY_ENABLED = '1'
    $env:DAMIR_GITHUB_TOKEN = ''
    $env:DAMIR_TIMEZONE = 'Asia/Tomsk'
    if ($IsWindows) {
        $fakeDocker = Join-Path $fakeBin 'docker.cmd'
        [IO.File]::WriteAllText($fakeDocker, "@echo off`r`nchcp 65001 >nul`r`necho %*>>`"%DAMIR_TEST_LOG%`"`r`necho %DAMIR_WORKSPACE%^|%DAMIR_SESSION_ID%^|%DAMIR_MEMORY_PROJECT%>>`"%DAMIR_TEST_WORKSPACE_LOG%`"`r`nif `"%DAMIR_TEST_FAIL%`"==`"1`" exit /b 23`r`necho test-sandbox-id`r`nexit /b 0`r`n")
        [IO.File]::WriteAllText((Join-Path $fakeBin 'git.cmd'), "@echo off`r`nexit /b 1`r`n")
    } else {
        $fakeGit = Join-Path $fakeBin 'git'
        [IO.File]::WriteAllText($fakeGit, "#!/bin/sh`nexit 1`n", [Text.UTF8Encoding]::new($false))
        & chmod 700 $fakeGit
        $fakeDocker = Join-Path $fakeBin 'docker'
        $mock = @'
#!/bin/sh
printf '%s\n' "$*" >> "$DAMIR_TEST_LOG"
printf '%s|%s|%s\n' "${DAMIR_WORKSPACE:-}" "${DAMIR_SESSION_ID:-}" "${DAMIR_MEMORY_PROJECT:-}" >> "$DAMIR_TEST_WORKSPACE_LOG"
if [ "${DAMIR_TEST_FAIL:-}" = 1 ]; then exit 23; fi
case "$*" in *shell-sandbox*) printf '%s\n' 'test-sandbox-id';; esac
exit 0
'@
        [IO.File]::WriteAllText($fakeDocker, $mock + "`n", [Text.UTF8Encoding]::new($false))
        & chmod 700 $fakeDocker
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось подготовить поддельный Docker.' }
    }
    $env:PATH = $fakeBin + [IO.Path]::PathSeparator + $oldPath
    $env:DAMIR_TEST_FAIL = ''
    $launcher = Join-Path $fixtureDir 'damir-launcher.ps1'
    $output = (& $pwsh -NoLogo -NoProfile -File $launcher run 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Команда run завершилась ошибкой: $output" }
    $calls = [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG)
    if ($calls.Count -ne 10 -or $calls[0] -notlike 'compose build web-search*' -or $calls[1] -notlike 'compose up -d --wait gateway postgres web-search*' -or $calls[2] -notlike 'compose up -d --wait memory-db embeddings*' -or $calls[3] -notlike 'compose exec -T embeddings ollama show*' -or $calls[4] -notlike 'compose build shell-sandbox*' -or $calls[5] -notlike '*shell-sandbox*' -or $calls[6] -notlike 'compose run --no-deps --rm --build agent*' -or $calls[7] -notlike 'stop test-sandbox-id*' -or $calls[8] -notlike 'volume rm damir-ipc-*' -or $calls[9] -notlike 'volume rm damir-mcp-*') { throw 'Неверная последовательность запуска.' }
    $key = [IO.File]::ReadAllText((Join-Path $fixtureDir '.uv-cache/damir-gateway-key')).Trim()
    $password = [IO.File]::ReadAllText((Join-Path $fixtureDir '.uv-cache/damir-db-password')).Trim()
    if ($key -notmatch '^sk-[0-9a-f]{64}$' -or $password -notmatch '^[0-9a-f]{64}$' -or $output.Contains($key) -or $output.Contains($password)) { throw 'Ошибка генерации или вывода локальных ключей.' }
    $todo = Join-Path $fixtureDir 'Todo List'
    $other = Join-Path $fixtureDir 'Other Project'
    [IO.Directory]::CreateDirectory($todo) | Out-Null
    [IO.Directory]::CreateDirectory($other) | Out-Null
    # GetTempPath can use an 8.3 alias while PowerShell expands the location.
    Push-Location -LiteralPath $todo
    try { $todo = (Get-Location).ProviderPath } finally { Pop-Location }
    Push-Location -LiteralPath $other
    try { $other = (Get-Location).ProviderPath } finally { Pop-Location }
    [IO.File]::WriteAllText($env:DAMIR_TEST_WORKSPACE_LOG, '')
    Push-Location -LiteralPath $todo
    try { $null = & $pwsh -NoLogo -NoProfile -File $launcher run } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Запуск проекта завершился ошибкой.' }
    $first = [IO.File]::ReadAllLines($env:DAMIR_TEST_WORKSPACE_LOG)[0].Split('|')
    [IO.File]::WriteAllText($env:DAMIR_TEST_WORKSPACE_LOG, '')
    Push-Location -LiteralPath $other
    try { $null = & $pwsh -NoLogo -NoProfile -File $launcher run } finally { Pop-Location }
    $second = [IO.File]::ReadAllLines($env:DAMIR_TEST_WORKSPACE_LOG)[0].Split('|')
    if ($first[0] -ne $todo -or $second[0] -ne $other -or $first[1] -notlike 'workspace-*' -or $first[1] -eq $second[1]) { throw 'Рабочие папки или истории не разделены.' }
    if ($first[2] -notlike 'workspace-*' -or $first[2] -eq $second[2]) { throw 'Память проектов не разделена.' }
    $env:DAMIR_SESSION_ID = 'new-session'
    [IO.File]::WriteAllText($env:DAMIR_TEST_WORKSPACE_LOG, '')
    Push-Location -LiteralPath $todo
    try { $null = & $pwsh -NoLogo -NoProfile -File $launcher run } finally { Pop-Location }
    $newSession = [IO.File]::ReadAllLines($env:DAMIR_TEST_WORKSPACE_LOG)[0].Split('|')
    if ($newSession[1] -ne 'new-session' -or $newSession[2] -ne $first[2]) { throw 'Новая сессия потеряла память проекта.' }
    $env:DAMIR_SESSION_ID = ''
    [IO.File]::WriteAllText($env:DAMIR_TEST_LOG, '')
    $backendOutput = (& $pwsh -NoLogo -NoProfile -File $launcher backend 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Backend launcher failed: $backendOutput" }
    $backendCalls = [IO.File]::ReadAllText($env:DAMIR_TEST_LOG)
    $backendKeyPath = Join-Path $fixtureDir '.uv-cache/damir-backend-key'
    if (-not [IO.File]::Exists($backendKeyPath)) { throw 'Backend key was not created.' }
    $backendKey = [IO.File]::ReadAllText($backendKeyPath).Trim()
    if ($backendKey -notmatch '^[0-9a-f]{64}$' -or $backendOutput.Contains($backendKey) -or
        -not $backendCalls.Contains('compose up -d --wait redis') -or
        -not $backendCalls.Contains('compose run --no-deps --rm --build --service-ports backend')) { throw 'Backend launch sequence or secret handling failed.' }
    $backendOutput = (& $pwsh -NoLogo -NoProfile -File $launcher backend 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllText($backendKeyPath).Trim() -ne $backendKey) { throw 'Backend key was not reused.' }
    [IO.File]::WriteAllText($env:DAMIR_TEST_WORKSPACE_LOG, '')
    Push-Location -LiteralPath $todo
    try { $null = & $pwsh -NoLogo -NoProfile -File $launcher } finally { Pop-Location }
    $defaultSession = [IO.File]::ReadAllLines($env:DAMIR_TEST_WORKSPACE_LOG)[0].Split('|')
    if ($LASTEXITCODE -ne 0 -or $defaultSession[1] -ne $first[1]) { throw 'Запуск без аргументов не открыл текущий проект.' }
    [IO.File]::WriteAllText($env:DAMIR_TEST_LOG, '')
    $null = & $pwsh -NoLogo -NoProfile -File $launcher login
    if ($LASTEXITCODE -ne 0) { throw 'Команда login завершилась ошибкой.' }
    $calls = [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG)
    if ($calls.Count -ne 2 -or $calls[0] -notlike '*--user 0 --entrypoint python gateway*' -or $calls[1] -notlike '*Authenticator*') { throw 'Неверная последовательность входа.' }
    if ([IO.File]::ReadAllText((Join-Path $fixtureDir '.uv-cache/damir-gateway-key')).Trim() -ne $key) { throw 'Существующий ключ был изменён.' }
    [IO.File]::WriteAllText($env:DAMIR_TEST_LOG, '')
    $env:DAMIR_TEST_FAIL = '1'
    $null = & $pwsh -NoLogo -NoProfile -File $launcher run
    if ($LASTEXITCODE -ne 23 -or [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG).Count -ne 3) { throw 'Ошибка Docker не остановила запуск.' }
    if ($IsWindows) {
        $legacy = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $env:PATH = $fakeBin + ';' + $fixtureDir + ';' + (Split-Path -Parent $pwsh) + ';' + $oldPath
        $env:DAMIR_TEST_FAIL = ''
        [IO.File]::WriteAllText($env:DAMIR_TEST_LOG, '')
        $legacyOutput = (& $legacy -NoLogo -NoProfile -Command 'damir login' 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG).Count -ne 2) { throw "Вход из Windows PowerShell 5.1 не работает: $legacyOutput" }
        [IO.File]::WriteAllText($env:DAMIR_TEST_WORKSPACE_LOG, '')
        Push-Location -LiteralPath $todo
        try { $null = & $legacy -NoLogo -NoProfile -Command 'damir' } finally { Pop-Location }
        $legacyWorkspace = [IO.File]::ReadAllLines($env:DAMIR_TEST_WORKSPACE_LOG)[0].Split('|')
        if ($LASTEXITCODE -ne 0 -or $legacyWorkspace[0] -ne $todo -or $legacyWorkspace[2] -ne $first[2]) { throw 'Переход на PowerShell 7 потерял рабочую папку или память.' }
        $env:DAMIR_TEST_FAIL = '1'
        # powershell.exe -Command otherwise converts a native nonzero exit to 1.
        $null = & $legacy -NoLogo -NoProfile -Command 'damir run; exit $LASTEXITCODE'
        if ($LASTEXITCODE -ne 23) { throw 'Переход с PowerShell 5.1 потерял код ошибки.' }
        $null = & (Join-Path $fixtureDir 'damir.cmd') run
        if ($LASTEXITCODE -ne 23) { throw 'damir.cmd потерял код ошибки.' }
        $env:DAMIR_TEST_FAIL = ''
        $env:PATH = $fakeBin + ';' + $fixtureDir
        [IO.File]::WriteAllText($env:DAMIR_TEST_LOG, '')
        $missingRuntime = (& $legacy -NoLogo -NoProfile -Command 'damir login' 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 1 -or -not $missingRuntime.Contains('PowerShell 7') -or [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG).Count -ne 0) { throw 'Отсутствие PowerShell 7 не обработано до запуска Docker.' }
    }
    Write-Host "Проверки запуска PowerShell пройдены: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
} finally {
    $env:PATH = $oldPath
    $env:DAMIR_TIMEZONE = $oldZone
    $env:DAMIR_TEST_LOG = $oldLog
    $env:DAMIR_TEST_FAIL = $oldFail
    $env:DAMIR_TEST_WORKSPACE_LOG = $oldWorkspaceLog
    $env:DAMIR_SESSION_ID = $oldSession
    $env:DAMIR_MEMORY_PROJECT = $oldMemoryProject
    $env:DAMIR_MEMORY_ENABLED = $oldMemoryEnabled
    $env:DAMIR_GITHUB_TOKEN = $oldGithubToken
    if ([IO.Directory]::Exists($fixtureDir)) { [IO.Directory]::Delete($fixtureDir, $true) }
}
