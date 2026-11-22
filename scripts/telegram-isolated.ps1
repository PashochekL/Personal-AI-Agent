# Dot-sourced by the launcher. All Docker operations belong to this invocation.
function Invoke-DamirIsolatedTelegram([string] $StateRoot) {
    $names = @('DAMIR_TELEGRAM_PROFILE_KEY', 'DAMIR_TELEGRAM_PROFILE_OWNER', 'DAMIR_TELEGRAM_PROFILE_PROJECT',
        'DAMIR_TELEGRAM_WORKSPACE_VOLUME', 'DAMIR_TELEGRAM_IPC_VOLUME', 'DAMIR_TELEGRAM_BACKENDS_FILE')
    $previous = @{}
    foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $backends = [Collections.Generic.List[string]]::new()
    $shells = [Collections.Generic.List[string]]::new()
    $ipc = [Collections.Generic.List[string]]::new()
    $runId = [guid]::NewGuid().ToString('N').Substring(0, 16)
    $routerName = "damir-tg-router-$runId"
    $manifest = Join-Path $StateRoot "telegram-profiles-$runId.json"
    $profiles = [Collections.Generic.List[object]]::new()
    $owner = if ([string]::IsNullOrEmpty($env:DAMIR_MEMORY_OWNER)) { 'local' } else { $env:DAMIR_MEMORY_OWNER }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $scopeBytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($owner + [char]0 + $env:DAMIR_MEMORY_PROJECT)) } finally { $hash.Dispose() }
    $scope = [BitConverter]::ToString($scopeBytes).Replace('-', '').ToLowerInvariant()
    $botId = ([long]($env:TELEGRAM_BOT_TOKEN.Split(':')[0])).ToString([Globalization.CultureInfo]::InvariantCulture)
    $keys = Join-Path $StateRoot 'telegram-keys'
    [IO.Directory]::CreateDirectory($keys) | Out-Null
    try {
        & docker compose build telegram-backend telegram-shell telegram | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать изолированные сервисы Telegram.' }
        foreach ($idText in $env:TELEGRAM_ALLOWED_USER_IDS.Split(',')) {
            $id = [long]$idText
            $backendName = "damir-tg-$runId-$id"
            $shellName = "damir-tg-shell-$runId-$id"
            $env:DAMIR_TELEGRAM_PROFILE_KEY = Get-DamirSecret (Join-Path $keys "$scope-$botId-$id.key") ''
            $env:DAMIR_TELEGRAM_PROFILE_OWNER = 'telegram:' + $botId + ':' + $id
            $env:DAMIR_TELEGRAM_PROFILE_PROJECT = "telegram-$scope"
            $env:DAMIR_TELEGRAM_WORKSPACE_VOLUME = "damir-tg-data-$scope-$botId-$id"
            $env:DAMIR_TELEGRAM_IPC_VOLUME = "damir-tg-ipc-$runId-$id"
            $ipc.Add($env:DAMIR_TELEGRAM_IPC_VOLUME)
            $shells.Add($shellName)
            $result = & docker compose run -d --rm --no-deps --name $shellName telegram-shell | Select-Object -Last 1
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($result)) { throw 'Не удалось запустить песочницу аккаунта.' }
            $backends.Add($backendName)
            $result = & docker compose run -d --rm --no-deps --name $backendName telegram-backend | Select-Object -Last 1
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($result)) { throw 'Не удалось запустить сервер аккаунта.' }
            $profiles.Add([pscustomobject]@{ user_id = $id; url = "http://${backendName}:8080/"; api_key = $env:DAMIR_TELEGRAM_PROFILE_KEY;
                owner_id = $env:DAMIR_TELEGRAM_PROFILE_OWNER; project_id = $env:DAMIR_TELEGRAM_PROFILE_PROJECT })
        }
        [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $profiles.ToArray() -Depth 4 -Compress), [Text.UTF8Encoding]::new($false))
        $env:DAMIR_TELEGRAM_BACKENDS_FILE = [IO.Path]::GetFullPath($manifest)
        Write-Host 'Telegram: у каждого разрешённого аккаунта отдельные файлы, память и диалоги. Остановка: Ctrl+C.'
        & docker compose run --no-deps --rm --name $routerName telegram | Out-Host
        return $LASTEXITCODE
    } finally {
        & docker stop --time 20 $routerName *> $null
        foreach ($name in $backends) { & docker stop --time 40 $name *> $null }
        foreach ($name in $shells) { & docker stop --time 10 $name *> $null }
        foreach ($volume in $ipc) { & docker volume rm $volume *> $null }
        if ([IO.File]::Exists($manifest)) { [IO.File]::Delete($manifest) }
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
}
