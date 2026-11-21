#requires -Version 7.0
param(
    [ValidateSet('auth-check', 'login', 'run', 'backend')]
    [string] $Command = 'run'
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$invocationDir = (Get-Location).ProviderPath
$changedEnvironment = @('DAMIR_CLI_BACKEND_URL', 'DAMIR_BACKEND_API_KEY', 'DAMIR_TIMEZONE', 'DAMIR_GATEWAY_CONFIG_REV', 'LITELLM_MASTER_KEY', 'DAMIR_POSTGRES_PASSWORD', 'DAMIR_WORKSPACE', 'DAMIR_SESSION_ID', 'DAMIR_MEMORY_PROJECT', 'DAMIR_SANDBOX_VOLUME', 'DAMIR_MCP_VOLUME', 'DAMIR_GITHUB_MCP', 'DAMIR_GITHUB_TOKEN', 'GCM_CREDENTIAL_STORE', 'GCM_INTERACTIVE', 'COMPOSE_PROGRESS', 'COMPOSE_IGNORE_ORPHANS')
$previousEnvironment = @{}
foreach ($name in $changedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location -LiteralPath $projectDir
try {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker Desktop и Docker Compose не найдены.'
    }

    if ([string]::IsNullOrWhiteSpace($env:DAMIR_TIMEZONE)) {
        $zoneId = [TimeZoneInfo]::Local.Id
        $ianaId = ''
        if ($PSVersionTable.PSVersion -ge [version]'7.2' -and [TimeZoneInfo]::TryConvertWindowsIdToIanaId($zoneId, [ref] $ianaId)) {
            $env:DAMIR_TIMEZONE = $ianaId
        } elseif ($zoneId -eq 'UTC' -or $zoneId.Contains('/')) {
            $env:DAMIR_TIMEZONE = $zoneId
        } else {
            $env:DAMIR_TIMEZONE = 'UTC'
            [Console]::Error.WriteLine('Не удалось определить часовой пояс; используется UTC. Можно задать DAMIR_TIMEZONE.')
        }
    }
    $env:DAMIR_GATEWAY_CONFIG_REV = (Get-FileHash -LiteralPath (Join-Path $projectDir 'config/litellm.yaml') -Algorithm SHA256).Hash
    $stateDir = Join-Path $projectDir '.uv-cache'
    [IO.Directory]::CreateDirectory($stateDir) | Out-Null

    function Get-DamirSecret([string] $path, [string] $prefix) {
        if (-not [IO.File]::Exists($path)) {
            $bytes = [byte[]]::new(32)
            $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
            try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
            $value = $prefix + [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
            try {
                $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $writer = [IO.StreamWriter]::new($file, [Text.UTF8Encoding]::new($false))
                    try { $writer.WriteLine($value) } finally { $writer.Dispose() }
                } finally { $file.Dispose() }
            } catch [IO.IOException] {
                if (-not [IO.File]::Exists($path)) { throw }
            }
        }
        $stored = [IO.File]::ReadAllText($path).Trim()
        if ([string]::IsNullOrWhiteSpace($stored)) { throw 'Локальный файл настроек пуст. Исправь его вручную.' }
        return $stored
    }

    $env:LITELLM_MASTER_KEY = Get-DamirSecret (Join-Path $stateDir 'damir-gateway-key') 'sk-'
    $env:DAMIR_POSTGRES_PASSWORD = Get-DamirSecret (Join-Path $stateDir 'damir-db-password') ''
    $env:GCM_CREDENTIAL_STORE = 'wincredman'
    $env:GCM_INTERACTIVE = 'Never'
    $githubKey = "protocol=https`nhost=damir-github.local`nusername=damir`n`n"

    switch ($Command) {
        { $_ -in 'login', 'auth-check' } {
            Write-Host 'Вход в ChatGPT по коду устройства через LiteLLM.'
            & docker compose run --rm --no-deps --user 0 --entrypoint python gateway -c "import os; os.chown('/tokens/chatgpt', 65532, 65532); os.chmod('/tokens/chatgpt', 0o700)"
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            & docker compose run --rm --no-deps --entrypoint python gateway -u -c "from litellm.llms.chatgpt.authenticator import Authenticator; Authenticator().get_access_token(); print('Вход завершён. Сессия сохранена в отдельном томе Дамира.')"
            exit $LASTEXITCODE
        }
        { $_ -in 'run', 'backend' } {
            $env:COMPOSE_PROGRESS = 'quiet'
            $env:COMPOSE_IGNORE_ORPHANS = 'true'
            if ([string]::Equals($invocationDir.TrimEnd([IO.Path]::DirectorySeparatorChar), $projectDir.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
                $env:DAMIR_WORKSPACE = Join-Path $projectDir 'workspace'
                [IO.Directory]::CreateDirectory($env:DAMIR_WORKSPACE) | Out-Null
                if ([string]::IsNullOrWhiteSpace($env:DAMIR_SESSION_ID)) { $env:DAMIR_SESSION_ID = 'local' }
            } else {
                $env:DAMIR_WORKSPACE = $invocationDir
                if ([string]::IsNullOrWhiteSpace($env:DAMIR_SESSION_ID)) {
                    $hash = [Security.Cryptography.SHA256]::Create()
                    try { $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($invocationDir)) } finally { $hash.Dispose() }
                    $env:DAMIR_SESSION_ID = 'workspace-' + [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
                }
            }
            $env:DAMIR_SANDBOX_VOLUME = 'damir-ipc-' + [Guid]::NewGuid().ToString('N')
            if ([string]::IsNullOrWhiteSpace($env:DAMIR_MEMORY_PROJECT)) {
                $memoryHash = [Security.Cryptography.SHA256]::Create()
                try { $memoryBytes = $memoryHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:DAMIR_WORKSPACE)) } finally { $memoryHash.Dispose() }
                $env:DAMIR_MEMORY_PROJECT = 'workspace-' + [BitConverter]::ToString($memoryBytes).Replace('-', '').ToLowerInvariant()
            }
            $env:DAMIR_MCP_VOLUME = 'damir-mcp-' + [Guid]::NewGuid().ToString('N')
            if ([string]::IsNullOrWhiteSpace($env:DAMIR_GITHUB_TOKEN)) {
                try {
                    $saved = $githubKey | & git credential-manager get 2>$null
                    if ($LASTEXITCODE -eq 0) {
                        $passwordLine = $saved | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
                        if ($passwordLine) { $env:DAMIR_GITHUB_TOKEN = $passwordLine.Substring(9) }
                    }
                } catch { }
            }
            $env:DAMIR_GITHUB_MCP = if ([string]::IsNullOrWhiteSpace($env:DAMIR_GITHUB_TOKEN)) { '0' } else { '1' }
            if ([string]::IsNullOrWhiteSpace($env:DAMIR_BACKEND_API_KEY)) {
                $env:DAMIR_BACKEND_API_KEY = Get-DamirSecret (Join-Path $stateDir 'damir-backend-key') ''
            }
            $sandboxId = $null
            $githubId = $null
            $backendId = $null
            try {
                & docker compose build web-search
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                & docker compose up -d --wait gateway postgres web-search
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                & docker compose up -d --wait redis
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                if ($env:DAMIR_MEMORY_ENABLED -ne '0') {
                    & docker compose up -d --wait memory-db embeddings
                    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    & docker compose exec -T embeddings ollama show embeddinggemma:300m 2>$null | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        & docker compose exec -T embeddings ollama pull embeddinggemma:300m
                        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    }
                }
                & docker compose build shell-sandbox
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                $sandboxId = (& docker compose run -d --rm --no-deps shell-sandbox | Select-Object -Last 1)
                if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sandboxId)) { throw 'Не удалось запустить песочницу.' }
                if ($env:DAMIR_GITHUB_MCP -eq '1') {
                    & docker compose build github-mcp
                    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    $githubId = (& docker compose run -d --rm --no-deps github-mcp | Select-Object -Last 1)
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($githubId)) { throw 'Не удалось запустить GitHub MCP.' }
                }
                if ($Command -eq 'backend') {
                    Write-Host 'Backend: http://127.0.0.1:8080 (порт можно задать DAMIR_BACKEND_PORT). Остановка: Ctrl+C.'
                    & docker compose run --no-deps --rm --build --service-ports backend
                    exit $LASTEXITCODE
                }
                while ($true) {
                    $backendName = 'damir-backend-' + $env:DAMIR_SANDBOX_VOLUME.Substring('damir-ipc-'.Length)
                    $backendId = (& docker compose run -d --rm --no-deps --build --name $backendName backend | Select-Object -Last 1)
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($backendId)) { throw 'Не удалось запустить backend.' }
                    $env:DAMIR_CLI_BACKEND_URL = "http://${backendName}:8080"
                    & docker compose run --no-deps --rm --build cli
                    $agentExit = $LASTEXITCODE
                    & docker stop --time 40 $backendId 2>$null | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'Не удалось остановить backend.' }
                    $backendId = $null
                    if ($agentExit -eq 71) {
                        $secret = Read-Host 'Вставь personal access token GitHub (ввод скрыт)' -AsSecureString
                        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
                        try { $githubToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
                        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr); $secret.Dispose() }
                        if ([string]::IsNullOrWhiteSpace($githubToken)) { Write-Host 'Токен не введён.'; continue }
                        $stored = $false
                        try {
                            "protocol=https`nhost=damir-github.local`nusername=damir`npassword=$githubToken`n`n" | & git credential-manager store
                            if ($LASTEXITCODE -ne 0) { throw 'Ошибка Git Credential Manager.' }
                            $env:DAMIR_GITHUB_TOKEN = $githubToken
                            $stored = $true
                        } catch {
                            [Console]::Error.WriteLine('Не удалось сохранить токен. Проверь, что Git Credential Manager установлен.')
                        } finally { $githubToken = $null }
                        if (-not $stored) { continue }
                        if ($githubId) { & docker stop $githubId 2>$null | Out-Null; $githubId = $null }
                        & docker compose build github-mcp
                        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                        $githubId = (& docker compose run -d --rm --no-deps github-mcp | Select-Object -Last 1)
                        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($githubId)) { throw 'Не удалось запустить GitHub MCP.' }
                        $env:DAMIR_GITHUB_MCP = '1'
                        Write-Host 'Токен сохранён в Credential Manager. Переподключаю GitHub.'
                        continue
                    }
                    if ($agentExit -eq 72) {
                        try {
                            $githubKey | & git credential-manager erase
                            if ($LASTEXITCODE -ne 0) { throw 'Ошибка Git Credential Manager.' }
                        } catch {
                            [Console]::Error.WriteLine('Не удалось удалить токен из Credential Manager.')
                            continue
                        }
                        $env:DAMIR_GITHUB_TOKEN = ''
                        $env:DAMIR_GITHUB_MCP = '0'
                        if ($githubId) { & docker stop $githubId 2>$null | Out-Null; $githubId = $null }
                        Write-Host 'Дамир отключён от GitHub; токен удалён из Credential Manager.'
                        continue
                    }
                    exit $agentExit
                }
            } finally {
                if ($backendId) { & docker stop --time 40 $backendId 2>$null | Out-Null }
                if ($sandboxId) { & docker stop $sandboxId 2>$null | Out-Null }
                if ($githubId) { & docker stop $githubId 2>$null | Out-Null }
                & docker volume rm $env:DAMIR_SANDBOX_VOLUME 2>$null | Out-Null
                & docker volume rm $env:DAMIR_MCP_VOLUME 2>$null | Out-Null
            }
        }
    }
} finally {
    foreach ($name in $changedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
