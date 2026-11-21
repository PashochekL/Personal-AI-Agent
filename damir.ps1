#requires -Version 7.0
param(
    [ValidateSet('auth-check', 'login', 'run')]
    [string] $Command = 'run'
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$invocationDir = (Get-Location).ProviderPath
$changedEnvironment = @('DAMIR_TIMEZONE', 'DAMIR_GATEWAY_CONFIG_REV', 'LITELLM_MASTER_KEY', 'DAMIR_POSTGRES_PASSWORD', 'DAMIR_WORKSPACE', 'DAMIR_SESSION_ID', 'DAMIR_SANDBOX_VOLUME', 'COMPOSE_PROGRESS', 'COMPOSE_IGNORE_ORPHANS')
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

    switch ($Command) {
        { $_ -in 'login', 'auth-check' } {
            Write-Host 'Вход в ChatGPT по коду устройства через LiteLLM.'
            & docker compose run --rm --no-deps --user 0 --entrypoint python gateway -c "import os; os.chown('/tokens/chatgpt', 65532, 65532); os.chmod('/tokens/chatgpt', 0o700)"
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            & docker compose run --rm --no-deps --entrypoint python gateway -u -c "from litellm.llms.chatgpt.authenticator import Authenticator; Authenticator().get_access_token(); print('Вход завершён. Сессия сохранена в отдельном томе Дамира.')"
            exit $LASTEXITCODE
        }
        'run' {
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
            $sandboxId = $null
            try {
                & docker compose up -d --wait gateway postgres
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                & docker compose build shell-sandbox
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                $sandboxId = (& docker compose run -d --rm --no-deps shell-sandbox | Select-Object -Last 1)
                if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sandboxId)) { throw 'Не удалось запустить песочницу.' }
                & docker compose run --no-deps --rm --build agent
                exit $LASTEXITCODE
            } finally {
                if ($sandboxId) { & docker stop $sandboxId 2>$null | Out-Null }
                & docker volume rm $env:DAMIR_SANDBOX_VOLUME 2>$null | Out-Null
            }
        }
    }
} finally {
    foreach ($name in $changedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
