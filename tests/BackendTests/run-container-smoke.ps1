#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repoDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = 'damir-backend-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$fixture = Join-Path $repoDir ".uv-cache/$prefix"
$settings = @{
    LITELLM_MASTER_KEY = 'sk-' + [Guid]::NewGuid().ToString('N')
    DAMIR_POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N')
    DAMIR_BACKEND_API_KEY = [Guid]::NewGuid().ToString('N')
    DAMIR_BACKEND_PORT = '0'; DAMIR_TASK_TIMEOUT_SECONDS = '5'; DAMIR_MEMORY_ENABLED = '0'; DAMIR_MEMORY_OWNER = $prefix
    DAMIR_MEMORY_PROJECT = 'container-smoke'; DAMIR_WORKSPACE = $fixture
    DAMIR_SANDBOX_VOLUME = "$prefix-ipc"; DAMIR_MCP_VOLUME = "$prefix-mcp"
}
$previous = @{}
foreach ($name in $settings.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
function Invoke-SmokeCompose([string[]] $Arguments) {
    $output = & docker compose --project-name $prefix --profile backend -f docker-compose.yml -f (Join-Path $fixture 'override.yml') @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Container smoke operation failed.' }
    return $output
}
Push-Location -LiteralPath $repoDir
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'override.yml'), "services:`n  backend:`n    image: personal-ai-agent-backend`n")
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process') }
    $null = Invoke-SmokeCompose -Arguments @('config', '--quiet')
    $null = Invoke-SmokeCompose -Arguments @('build', 'backend')
    $null = Invoke-SmokeCompose -Arguments @('up', '-d', '--wait', 'postgres', 'redis')
    $null = Invoke-SmokeCompose -Arguments @('up', '-d', '--no-build', 'backend')
    $address = (Invoke-SmokeCompose -Arguments @('port', 'backend', '8080')).Trim()
    $base = "http://$address"
    Write-Host "Testing production API on $base"
    $headers = @{ Authorization = 'Bearer ' + $settings.DAMIR_BACKEND_API_KEY }
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { $null = Invoke-RestMethod "$base/health/ready" -Headers $headers -TimeoutSec 2; $ready = $true; break }
        catch { $lastFailure = $_.Exception.Message; Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready) { throw "Production container did not become ready: $lastFailure" }
    $session = Invoke-RestMethod "$base/sessions" -Method Post -Headers $headers
    $body = @{ session_id = $session.session_id; user_message = 'synthetic smoke request' } | ConvertTo-Json -Compress
    $task = Invoke-RestMethod "$base/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    for ($attempt = 0; $attempt -lt 80 -and $task.status -in 'PENDING', 'RUNNING'; $attempt++) {
        Start-Sleep -Milliseconds 200
        $task = Invoke-RestMethod "$base/tasks/$($task.task_id)" -Headers $headers
    }
    # No Gateway is started in this fixture. The real Core must persist a sanitized failure.
    if ($task.status -ne 'FAILED' -or [string]::IsNullOrWhiteSpace($task.result.error)) { throw 'Production container did not persist gateway failure.' }
    Write-Host 'Production container checks passed: Compose validation, Redis permissions, authenticated readiness, task admission, Core failure persistence.'
} finally {
    if ($prefix -notmatch '^damir-backend-smoke-[a-f0-9]{12}$') { throw 'Invalid fixture project name.' }
    $null = Invoke-SmokeCompose -Arguments @('down', '--volumes', '--timeout', '40')
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
    if ([IO.Directory]::Exists($fixture)) {
        # Only the one file written by this fixture is removed; no recursive deletion.
        [IO.File]::Delete((Join-Path $fixture 'override.yml'))
        [IO.Directory]::Delete($fixture)
    }
}
