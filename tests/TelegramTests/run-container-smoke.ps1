#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$volume = 'damir-telegram-check-' + [guid]::NewGuid().ToString('N')
$image = 'personal-ai-agent-telegram-test'
$settings = @{ LITELLM_MASTER_KEY = 'synthetic-gateway-key'; DAMIR_POSTGRES_PASSWORD = 'synthetic-password' }
$previous = @{}
foreach ($name in $settings.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location -LiteralPath $repo
try {
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process') }
    & docker compose --profile telegram config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Telegram Compose configuration failed.' }
    & docker build -f docker/Dockerfile.telegram -t $image .
    if ($LASTEXITCODE -ne 0) { throw 'Telegram image build failed.' }
    $null = & docker volume create --label personal-ai-agent.telegram-test=1 $volume
    if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated state volume.' }
    & docker run --rm --network none --read-only --mount "type=volume,source=$volume,target=/state" --entrypoint /bin/sh $image -c 'test -w /state && touch /state/write-check'
    if ($LASTEXITCODE -ne 0) { throw 'Unprivileged Telegram container cannot write its state volume.' }
    $output = & docker run --rm --network none --read-only --mount "type=volume,source=$volume,target=/state" $image 2>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or -not $output.Contains('Некорректный токен')) { throw 'Missing Telegram credentials were not rejected before network access.' }
    Write-Host 'Telegram container checks passed: Compose, build, unprivileged persistent state and fail-closed configuration, with network disabled.'
} finally {
    if ($volume -notmatch '^damir-telegram-check-[a-f0-9]{32}$') { throw 'Invalid test volume name.' }
    & docker volume rm $volume *> $null
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
