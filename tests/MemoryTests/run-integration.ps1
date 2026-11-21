#requires -Version 7.0
param([switch] $WithEmbeddings)
$ErrorActionPreference = 'Stop'
$repoDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = 'damir-memory-check-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$cacheDir = Join-Path $repoDir '.uv-cache'
$fixtureDir = Join-Path $cacheDir $prefix
$password = [Guid]::NewGuid().ToString('N')
$proxyKey = 'sk-' + [Guid]::NewGuid().ToString('N')
$containers = [Collections.Generic.List[string]]::new()
$networkCreated = $false
$changed = @('DAMIR_MEMORY_TEST_DATABASE_URL', 'DAMIR_MEMORY_TEST_EMBEDDING_URL', 'DAMIR_MEMORY_TEST_EMBEDDING_KEY', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT')
$previous = @{}
foreach ($name in $changed) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

function Invoke-TestDocker([string[]] $Arguments) {
    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Docker memory test operation failed.' }
    return $output
}
function Get-TestPort([string] $Name, [string] $Port) {
    $address = Invoke-TestDocker -Arguments @('port', $Name, $Port)
    return ($address.Trim() -split ':')[-1]
}

Push-Location -LiteralPath $repoDir
try {
    [IO.Directory]::CreateDirectory($fixtureDir) | Out-Null
    $null = Invoke-TestDocker -Arguments @('network', 'create', '--label', 'personal-ai-agent.memory-test=1', $prefix)
    $networkCreated = $true
    $databaseName = "$prefix-db"
    $containers.Add($databaseName)
    $null = Invoke-TestDocker -Arguments @('run', '--rm', '-d', '--name', $databaseName, '--network', $prefix,
        '--label', 'personal-ai-agent.memory-test=1', '--memory=256m',
        '-e', 'POSTGRES_USER=memory_test', '-e', 'POSTGRES_DB=memory_test', '-e', "POSTGRES_PASSWORD=$password",
        '-p', '127.0.0.1::5432', 'pgvector/pgvector:0.8.7-pg17-bookworm@sha256:b513c48566e4777cd3370451400000c78eda6a08131d68f4c31d4a76f27bd8fd')
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $databaseName pg_isready -U memory_test -d memory_test *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Test PostgreSQL did not become ready.' }
    $dbPort = Get-TestPort $databaseName '5432/tcp'
    $env:DAMIR_MEMORY_TEST_DATABASE_URL = "Host=127.0.0.1;Port=$dbPort;Database=memory_test;Username=memory_test;Password=$password"
    $env:DAMIR_MEMORY_TEST_EMBEDDING_URL = $null
    $env:DAMIR_MEMORY_TEST_EMBEDDING_KEY = $null

    if ($WithEmbeddings) {
        $embeddingName = "$prefix-embeddings"
        $containers.Add($embeddingName)
        $null = Invoke-TestDocker -Arguments @('run', '--rm', '-d', '--name', $embeddingName, '--network', $prefix,
            '--label', 'personal-ai-agent.memory-test=1', '--memory=2g', '--cpus=2', '-e', 'OLLAMA_NO_CLOUD=1',
            '-v', 'personal-ai-agent_damir_embeddings:/root/.ollama', 'ollama/ollama:0.40.17@sha256:ccc42bcd3d5fcf7ab0d532b300382154e1a4da56fc666bdb313b78ebdd912754')
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & docker exec $embeddingName ollama list *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'Test Ollama did not become ready.' }
        & docker exec $embeddingName ollama show embeddinggemma:300m *> $null
        if ($LASTEXITCODE -ne 0) { $null = Invoke-TestDocker -Arguments @('exec', $embeddingName, 'ollama', 'pull', 'embeddinggemma:300m') }
        $configPath = Join-Path $fixtureDir 'gateway.yaml'
        [IO.File]::WriteAllText($configPath, @"
model_list:
  - model_name: damir-memory-embedding
    litellm_params:
      model: ollama/embeddinggemma:300m
      api_base: http://${embeddingName}:11434
general_settings:
  master_key: os.environ/LITELLM_MASTER_KEY
"@)
        $keyPath = Join-Path $fixtureDir 'gateway.env'
        [IO.File]::WriteAllText($keyPath, "LITELLM_MASTER_KEY=$proxyKey")
        $gatewayName = "$prefix-gateway"
        $containers.Add($gatewayName)
        $null = Invoke-TestDocker -Arguments @('run', '--rm', '-d', '--name', $gatewayName, '--network', $prefix,
            '--label', 'personal-ai-agent.memory-test=1', '--memory=768m', '--env-file', $keyPath,
            '-v', "${configPath}:/app/memory-test.yaml:ro", '-p', '127.0.0.1::4000',
            'ghcr.io/berriai/litellm@sha256:679c95daabea3e8775d42c744e6771b8a8cb47b3ea046ab9fb6c9deeb35d3dc3',
            '--config', '/app/memory-test.yaml', '--port', '4000')
        $gatewayPort = Get-TestPort $gatewayName '4000/tcp'
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try { $null = Invoke-WebRequest "http://127.0.0.1:$gatewayPort/health/liveliness" -TimeoutSec 2; $ready = $true; break }
            catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { throw 'Test LiteLLM did not become ready.' }
        $env:DAMIR_MEMORY_TEST_EMBEDDING_URL = "http://127.0.0.1:$gatewayPort"
        $env:DAMIR_MEMORY_TEST_EMBEDDING_KEY = $proxyKey
    }
    $env:DOTNET_CLI_HOME = Join-Path $repoDir '.dotnet'
    $env:NUGET_PACKAGES = Join-Path $cacheDir 'nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & dotnet run --disable-build-servers --project tests/MemoryTests/MemoryTests.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Memory integration checks failed.' }
} catch {
    # These containers receive only synthetic test data and disposable credentials.
    if ($WithEmbeddings -and $gatewayName) { & docker logs --tail 35 $gatewayName 2>&1 | Write-Host }
    if ($WithEmbeddings -and $embeddingName) { & docker logs --tail 15 $embeddingName 2>&1 | Where-Object { $_.ToString() -notmatch 'server config|env=' } | Write-Host }
    throw
} finally {
    foreach ($container in $containers) { & docker stop --timeout 10 $container *> $null }
    if ($networkCreated) { & docker network rm $prefix *> $null }
    foreach ($name in $changed) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureDir)
    if (-not $resolvedFixture.StartsWith([IO.Path]::GetFullPath($cacheDir) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ([IO.Directory]::Exists($resolvedFixture)) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
