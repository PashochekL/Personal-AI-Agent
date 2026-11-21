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
$pwsh = (Get-Command pwsh -CommandType Application).Source | Select-Object -First 1
try {
    $fakeBin = Join-Path $fixtureDir 'bin'
    $configDir = Join-Path $fixtureDir 'config'
    [IO.Directory]::CreateDirectory($fakeBin) | Out-Null
    [IO.Directory]::CreateDirectory($configDir) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoDir 'damir.ps1') -Destination $fixtureDir
    [IO.File]::WriteAllText((Join-Path $configDir 'litellm.yaml'), 'model_list: []')
    $env:DAMIR_TEST_LOG = Join-Path $fixtureDir 'calls.log'
    $env:DAMIR_TEST_WORKSPACE_LOG = Join-Path $fixtureDir 'workspace.log'
    $env:DAMIR_SESSION_ID = ''
    $env:DAMIR_TIMEZONE = 'Asia/Tomsk'
    if ($IsWindows) {
        $fakeDocker = Join-Path $fakeBin 'docker.cmd'
        [IO.File]::WriteAllText($fakeDocker, "@echo off`r`necho %*>>`"%DAMIR_TEST_LOG%`"`r`necho %DAMIR_WORKSPACE%^|%DAMIR_SESSION_ID%>>`"%DAMIR_TEST_WORKSPACE_LOG%`"`r`nif `"%DAMIR_TEST_FAIL%`"==`"1`" exit /b 23`r`necho test-sandbox-id`r`nexit /b 0`r`n")
    } else {
        $fakeDocker = Join-Path $fakeBin 'docker'
        $mock = @'
#!/bin/sh
printf '%s\n' "$*" >> "$DAMIR_TEST_LOG"
printf '%s|%s\n' "${DAMIR_WORKSPACE:-}" "${DAMIR_SESSION_ID:-}" >> "$DAMIR_TEST_WORKSPACE_LOG"
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
    $launcher = Join-Path $fixtureDir 'damir.ps1'
    $output = (& $pwsh -NoLogo -NoProfile -File $launcher run 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Команда run завершилась ошибкой: $output" }
    $calls = [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG)
    if ($calls.Count -ne 6 -or $calls[0] -notlike 'compose up -d --wait gateway postgres*' -or $calls[1] -notlike 'compose build shell-sandbox*' -or $calls[2] -notlike '*shell-sandbox*' -or $calls[3] -notlike 'compose run --no-deps --rm --build agent*' -or $calls[4] -notlike 'stop test-sandbox-id*' -or $calls[5] -notlike 'volume rm damir-ipc-*') { throw 'Неверная последовательность запуска.' }
    $key = [IO.File]::ReadAllText((Join-Path $fixtureDir '.uv-cache/damir-gateway-key')).Trim()
    $password = [IO.File]::ReadAllText((Join-Path $fixtureDir '.uv-cache/damir-db-password')).Trim()
    if ($key -notmatch '^sk-[0-9a-f]{64}$' -or $password -notmatch '^[0-9a-f]{64}$' -or $output.Contains($key) -or $output.Contains($password)) { throw 'Ошибка генерации или вывода локальных ключей.' }
    $todo = Join-Path $fixtureDir 'Todo List'
    $other = Join-Path $fixtureDir 'Other Project'
    [IO.Directory]::CreateDirectory($todo) | Out-Null
    [IO.Directory]::CreateDirectory($other) | Out-Null
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
    if ($LASTEXITCODE -ne 23 -or [IO.File]::ReadAllLines($env:DAMIR_TEST_LOG).Count -ne 2) { throw 'Ошибка Docker не остановила запуск.' }
    Write-Host "Проверки запуска PowerShell пройдены: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
} finally {
    $env:PATH = $oldPath
    $env:DAMIR_TIMEZONE = $oldZone
    $env:DAMIR_TEST_LOG = $oldLog
    $env:DAMIR_TEST_FAIL = $oldFail
    $env:DAMIR_TEST_WORKSPACE_LOG = $oldWorkspaceLog
    $env:DAMIR_SESSION_ID = $oldSession
    if ([IO.Directory]::Exists($fixtureDir)) { [IO.Directory]::Delete($fixtureDir, $true) }
}
