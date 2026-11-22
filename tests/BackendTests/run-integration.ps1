#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repoDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = 'damir-backend-check-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$password = [Guid]::NewGuid().ToString('N')
$containers = [Collections.Generic.List[string]]::new()
$changed = @('DAMIR_BACKEND_TEST_DATABASE_URL', 'DAMIR_BACKEND_TEST_REDIS', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DAMIR_OBSERVABILITY_ENABLED')
$previous = @{}
foreach ($name in $changed) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
function Invoke-TestDocker([string[]] $Arguments) {
    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Docker backend test operation failed.' }
    return $output
}
Push-Location -LiteralPath $repoDir
try {
    $env:DAMIR_OBSERVABILITY_ENABLED = '0'
    $dbName = "$prefix-db"; $redisName = "$prefix-redis"
    $containers.Add($dbName)
    $null = Invoke-TestDocker -Arguments @('run', '--rm', '-d', '--name', $dbName,
        '--label', 'personal-ai-agent.backend-test=1', '--memory=256m',
        '-e', 'POSTGRES_USER=backend_test', '-e', 'POSTGRES_DB=backend_test', '-e', "POSTGRES_PASSWORD=$password",
        '-p', '127.0.0.1::5432', 'pgvector/pgvector:0.8.7-pg17-bookworm@sha256:b513c48566e4777cd3370451400000c78eda6a08131d68f4c31d4a76f27bd8fd')
    $containers.Add($redisName)
    $null = Invoke-TestDocker -Arguments @('run', '--rm', '-d', '--name', $redisName,
        '--label', 'personal-ai-agent.backend-test=1', '--memory=64m', '-p', '127.0.0.1::6379',
        'redis:7-alpine@sha256:7f7d30b7fdd09467bac58198a1f166dd69789d348d4fe859cccb20e0cf287fbe')
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $dbName pg_isready -U backend_test -d backend_test *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Test PostgreSQL did not become ready.' }
    $dbPort = ((Invoke-TestDocker -Arguments @('port', $dbName, '5432/tcp')).Trim() -split ':')[-1]
    $redisPort = ((Invoke-TestDocker -Arguments @('port', $redisName, '6379/tcp')).Trim() -split ':')[-1]
    $env:DAMIR_BACKEND_TEST_DATABASE_URL = "Host=127.0.0.1;Port=$dbPort;Database=backend_test;Username=backend_test;Password=$password"
    $env:DAMIR_BACKEND_TEST_REDIS = "127.0.0.1:$redisPort"
    $env:DOTNET_CLI_HOME = Join-Path $repoDir '.dotnet'
    $env:NUGET_PACKAGES = Join-Path $repoDir '.uv-cache/nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & dotnet run --disable-build-servers --project tests/BackendTests/BackendTests.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Backend integration checks failed.' }
} finally {
    foreach ($container in $containers) { & docker stop --timeout 10 $container *> $null }
    foreach ($name in $changed) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
