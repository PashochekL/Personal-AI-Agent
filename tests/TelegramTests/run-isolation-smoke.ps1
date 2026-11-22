#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = 'damir-tg-isolation-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$fixture = Join-Path $repo ".uv-cache/$prefix"
$settings = @{
    LITELLM_MASTER_KEY = 'synthetic-' + [guid]::NewGuid().ToString('N')
    DAMIR_POSTGRES_PASSWORD = [guid]::NewGuid().ToString('N')
    DAMIR_MEMORY_ENABLED = '0'; DEFAULT_MODEL = 'synthetic-chat'; DAMIR_GITHUB_MCP = '0'
    DAMIR_TASK_TIMEOUT_SECONDS = '15'; DAMIR_TELEGRAM_PROFILE_PROJECT = $prefix
    DAMIR_TELEGRAM_PROFILE_OWNER = ''; DAMIR_TELEGRAM_PROFILE_KEY = ''
    DAMIR_TELEGRAM_WORKSPACE_VOLUME = "$prefix-initial-data"; DAMIR_TELEGRAM_IPC_VOLUME = "$prefix-initial-ipc"
    DAMIR_SANDBOX_VOLUME = "$prefix-root-ipc"; DAMIR_MCP_VOLUME = "$prefix-root-mcp"
}
$previous = @{}; foreach ($name in $settings.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$containers = [Collections.Generic.List[string]]::new(); $volumes = [Collections.Generic.List[string]]::new()
function Compose([string[]] $Arguments, [switch] $Production) {
    $options = @('compose', '-p', $prefix, '--profile', 'telegram', '--profile', 'backend', '-f', 'docker-compose.yml')
    if (-not $Production) { $options += @('-f', (Join-Path $fixture 'override.yml')) }
    $result = & docker @options @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Isolation fixture Docker Compose operation failed.' }
    return $result
}
function Invoke-IsolationDocker([string[]] $Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Isolation fixture Docker operation failed.' }
    return $result
}
function Run-Task($Profile, [string] $Prompt) {
    $headers = @{ Authorization = 'Bearer ' + $Profile.Key }
    $session = Invoke-RestMethod "$($Profile.Url)/sessions" -Method Post -Headers $headers
    $body = @{ session_id = $session.session_id; user_message = $Prompt } | ConvertTo-Json -Compress
    $task = Invoke-RestMethod "$($Profile.Url)/tasks" -Method Post -Headers $headers -ContentType application/json -Body $body
    for ($i = 0; $i -lt 100 -and $task.status -in 'PENDING', 'RUNNING'; $i++) {
        Start-Sleep -Milliseconds 200
        $task = Invoke-RestMethod "$($Profile.Url)/tasks/$($task.task_id)" -Headers $headers
    }
    if ($task.status -ne 'COMPLETED' -or $task.result.response -notlike '*Exit code: 0*' -or $task.result.response -notlike "*private-$($Profile.Account)*") {
        throw "Isolated shell execution failed for synthetic account $($Profile.Account): $($task.result.error) $($task.result.response)"
    }
}
Push-Location -LiteralPath $repo
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'override.yml'), @'
services:
  telegram-backend:
    ports: !override ["127.0.0.1::8080"]
  gateway:
    image: mcr.microsoft.com/dotnet/sdk:10.0
    command: [/usr/share/powershell/pwsh, -NoProfile, -File, /fixture/gateway.ps1]
    volumes: !override
      - ./tests/BackendTests/container-gateway.ps1:/fixture/gateway.ps1:ro
    healthcheck:
      test: [CMD, /usr/share/powershell/pwsh, -NoProfile, -Command, "Invoke-WebRequest http://127.0.0.1:4000/models -ErrorAction Stop | Out-Null"]
      interval: 2s
      timeout: 5s
      retries: 20
'@)
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process') }
    $production = (Compose -Arguments @('config', '--format', 'json') -Production) -join "`n" | ConvertFrom-Json
    if (@($production.services.'telegram-backend'.ports).Where({ $null -ne $_ }).Count -ne 0) { throw 'Production account backend must not publish host ports.' }
    $null = Compose -Arguments @('build', 'telegram-backend', 'telegram-shell')
    $null = Compose -Arguments @('up', '-d', '--wait', 'postgres', 'redis', 'gateway')
    $profiles = @()
    foreach ($account in 'A', 'B') {
        $env:DAMIR_TELEGRAM_PROFILE_KEY = [guid]::NewGuid().ToString('N')
        $env:DAMIR_TELEGRAM_PROFILE_OWNER = "telegram:123456:$account"
        $env:DAMIR_TELEGRAM_WORKSPACE_VOLUME = "$prefix-data-$account".ToLowerInvariant()
        $env:DAMIR_TELEGRAM_IPC_VOLUME = "$prefix-ipc-$account".ToLowerInvariant()
        $volumes.Add($env:DAMIR_TELEGRAM_WORKSPACE_VOLUME); $volumes.Add($env:DAMIR_TELEGRAM_IPC_VOLUME)
        $shell = "$prefix-shell-$account".ToLowerInvariant(); $backend = "$prefix-backend-$account".ToLowerInvariant()
        $containers.Add($shell); $containers.Add($backend)
        $null = Compose -Arguments @('run', '-d', '--rm', '--no-deps', '--name', $shell, 'telegram-shell')
        $null = Compose -Arguments @('run', '-d', '--rm', '--no-deps', '--service-ports', '--name', $backend, 'telegram-backend')
        $port = ((Invoke-IsolationDocker -Arguments @('port', $backend, '8080/tcp')) | Select-Object -First 1).Trim()
        $profile = @{ Url = "http://$port"; Key = $env:DAMIR_TELEGRAM_PROFILE_KEY; Account = $account }
        $profiles += $profile
        $ready = $false
        for ($i = 0; $i -lt 70; $i++) {
            try { $identity = Invoke-RestMethod "$($profile.Url)/identity" -Headers @{ Authorization = 'Bearer ' + $profile.Key } -TimeoutSec 2; $ready = $true; break }
            catch { Start-Sleep -Milliseconds 300 }
        }
        if (-not $ready -or $identity.owner_id -ne $env:DAMIR_TELEGRAM_PROFILE_OWNER) { throw 'Synthetic account identity mismatch.' }
        foreach ($name in $shell, $backend) {
            $inspect = ((Invoke-IsolationDocker -Arguments @('inspect', $name)) -join "`n" | ConvertFrom-Json)[0]
            if (@($inspect.Mounts).Count -ne 2 -or @($inspect.Mounts | Where-Object { $_.Type -ne 'volume' -or $_.Name -notin $env:DAMIR_TELEGRAM_WORKSPACE_VOLUME, $env:DAMIR_TELEGRAM_IPC_VOLUME }).Count -gt 0) {
                throw 'Account container has a foreign mount.'
            }
            if ($name -eq $shell -and $inspect.HostConfig.NetworkMode -ne 'none') { throw 'Account shell has network access.' }
        }
    }
    # Repeat A after B wrote its file: each IPC channel and workspace must remain private.
    Run-Task $profiles[0] 'isolation-shell-A'; Run-Task $profiles[1] 'isolation-shell-B'; Run-Task $profiles[0] 'isolation-shell-A'
    Write-Host 'Docker isolation passed: separate authenticated backends, private workspace/IPC mounts, networkless shells, real Core shell execution and no peer files.'
} finally {
    if ($prefix -notmatch '^damir-tg-isolation-[a-f0-9]{12}$') { throw 'Unexpected fixture project.' }
    foreach ($name in $containers) { & docker stop --time 20 $name *> $null }
    $null = Compose -Arguments @('down', '--volumes', '--timeout', '20')
    foreach ($name in $volumes) { & docker volume rm $name *> $null }
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
    [IO.File]::Delete((Join-Path $fixture 'override.yml'))
    if ([IO.Directory]::Exists($fixture)) { [IO.Directory]::Delete($fixture) }
}
