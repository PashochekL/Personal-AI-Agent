#requires -Version 7.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This check exercises Windows DPAPI.' }
$repoDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureDir = Join-Path $repoDir ('.uv-cache/personal-launch-' + [Guid]::NewGuid().ToString('N'))
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$variables = @('PATH', 'TELEGRAM_BOT_TOKEN', 'TELEGRAM_ALLOWED_USER_IDS', 'TELEGRAM_ALLOWED_USER_ID', 'DAMIR_PERSONAL_TEST_LOG', 'DAMIR_PERSONAL_TEST_MARKER', 'DAMIR_TIMEZONE', 'DAMIR_MEMORY_ENABLED', 'DAMIR_GITHUB_TOKEN', 'DAMIR_TELEGRAM_AUTO_OPEN', 'DAMIR_BACKEND_API_KEY', 'DAMIR_SESSION_ID', 'DAMIR_MEMORY_PROJECT', 'DAMIR_SETTINGS_DIR')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    [IO.Directory]::CreateDirectory((Join-Path $fixtureDir 'bin')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $fixtureDir 'config')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoDir 'damir-launcher.ps1') -Destination $fixtureDir
    [IO.File]::WriteAllText((Join-Path $fixtureDir 'config/litellm.yaml'), 'model_list: []')
    $mockDocker = @'
@echo off
echo %*>>"%DAMIR_PERSONAL_TEST_LOG%"
if not "%*"=="compose run --no-deps --rm --build cli" goto done
if exist "%DAMIR_PERSONAL_TEST_MARKER%" goto second
echo first>"%DAMIR_PERSONAL_TEST_MARKER%"
exit /b 73
:second
echo %DAMIR_TELEGRAM_AUTO_OPEN% >"%DAMIR_PERSONAL_TEST_MARKER%"
:done
echo synthetic-container
exit /b 0
'@
    [IO.File]::WriteAllText((Join-Path $fixtureDir 'bin/docker.cmd'), $mockDocker.Replace("`n", "`r`n"))
    [IO.File]::WriteAllText((Join-Path $fixtureDir 'bin/git.cmd'), "@echo off`r`nexit /b 1`r`n")
    $harness = @'
param([switch] $AllowTokenPrompt, [string] $TestCommand = 'run')
$ErrorActionPreference = 'Stop'
function Read-Host([string] $Prompt, [switch] $AsSecureString) {
    if (-not $AllowTokenPrompt -or -not $AsSecureString) { throw 'Unexpected prompt: only one secure token prompt is allowed.' }
    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'prompts.log') -Value 'token'
    return ConvertTo-SecureString '123456:synthetic_personal_token_not_real' -AsPlainText -Force
}
& (Join-Path $PSScriptRoot 'damir-launcher.ps1') $TestCommand
exit $LASTEXITCODE
'@
    [IO.File]::WriteAllText((Join-Path $fixtureDir 'harness.ps1'), $harness, [Text.UTF8Encoding]::new($false))
    $env:PATH = (Join-Path $fixtureDir 'bin') + ';' + $env:PATH
    $env:TELEGRAM_BOT_TOKEN = ''; $env:TELEGRAM_ALLOWED_USER_ID = ''; $env:TELEGRAM_ALLOWED_USER_IDS = ''
    $env:DAMIR_MEMORY_ENABLED = '0'; $env:DAMIR_TIMEZONE = 'UTC'; $env:DAMIR_GITHUB_TOKEN = ''
    $env:DAMIR_TELEGRAM_AUTO_OPEN = ''; $env:DAMIR_BACKEND_API_KEY = ''; $env:DAMIR_SESSION_ID = ''; $env:DAMIR_MEMORY_PROJECT = ''
    $env:DAMIR_PERSONAL_TEST_LOG = Join-Path $fixtureDir 'docker.log'
    $env:DAMIR_PERSONAL_TEST_MARKER = Join-Path $fixtureDir 'restart.txt'
    $env:DAMIR_SETTINGS_DIR = Join-Path $fixtureDir 'settings'
    Push-Location -LiteralPath $fixtureDir
    try { $output = (& $pwsh -NoProfile -File ./harness.ps1 -AllowTokenPrompt 2>&1 | Out-String) }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "Personal setup failed: $output" }
    $encrypted = [IO.File]::ReadAllText((Join-Path $env:DAMIR_SETTINGS_DIR 'telegram-token.dpapi'))
    if ($encrypted.Contains('synthetic_personal_token') -or $output.Contains('synthetic_personal_token')) { throw 'Token exposed in storage or output.' }
    if ([IO.File]::ReadAllText($env:DAMIR_PERSONAL_TEST_MARKER).Trim() -ne '1') { throw 'Automatic handoff restart failed.' }
    if ([IO.File]::ReadAllText((Join-Path $fixtureDir 'prompts.log')).Trim() -ne 'token') { throw 'Expected exactly one secure token prompt.' }
    Write-Host 'PASS: first /tg securely saves a token, asks for no IDs and automatically reopens Telegram'
    Push-Location -LiteralPath $fixtureDir
    try { $output = (& $pwsh -NoProfile -File ./harness.ps1 2>&1 | Out-String) }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or $output.Contains('synthetic_personal_token')) { throw 'Saved token was not reused without prompting.' }
    Write-Host 'PASS: ordinary console startup reuses the saved personal Telegram token'
    Push-Location -LiteralPath $fixtureDir
    try { $output = (& $pwsh -NoProfile -File ./harness.ps1 -TestCommand telegram 2>&1 | Out-String) }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllText($env:DAMIR_PERSONAL_TEST_MARKER).Trim() -ne '1') { throw 'damir telegram did not request the automatic personal link.' }
    if ([IO.File]::ReadAllText($env:DAMIR_PERSONAL_TEST_LOG).Contains('telegram-backend')) { throw 'Personal Telegram unexpectedly launched isolated account infrastructure.' }
    Write-Host 'PASS: damir telegram requests the personal link without asking for IDs or a saved token'
    $otherInstall = Join-Path $fixtureDir 'other-install'
    [IO.Directory]::CreateDirectory((Join-Path $otherInstall 'config')) | Out-Null
    foreach ($file in @('damir-launcher.ps1', 'harness.ps1', 'config/litellm.yaml')) {
        Copy-Item -LiteralPath (Join-Path $fixtureDir $file) -Destination (Join-Path $otherInstall $file)
    }
    Push-Location -LiteralPath $otherInstall
    try { $output = (& $pwsh -NoProfile -File ./harness.ps1 -TestCommand telegram 2>&1 | Out-String) }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Another installation did not reuse the user settings.' }
    Write-Host 'PASS: another checkout reuses the system credential without prompting'
    [IO.File]::WriteAllText((Join-Path $fixtureDir '.uv-cache/telegram-token.dpapi'), $encrypted)
    [IO.File]::Delete((Join-Path $env:DAMIR_SETTINGS_DIR 'telegram-token.dpapi'))
    Push-Location -LiteralPath $fixtureDir
    try { $output = (& $pwsh -NoProfile -File ./harness.ps1 -TestCommand telegram 2>&1 | Out-String) }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists((Join-Path $env:DAMIR_SETTINGS_DIR 'telegram-token.dpapi'))) { throw 'Legacy local token was not migrated to the user settings.' }
    Write-Host 'PASS: legacy encrypted credential migrates to the system settings'
    foreach ($directory in @([Environment]::GetFolderPath('UserProfile'), [IO.Path]::GetPathRoot($fixtureDir))) {
        [IO.File]::WriteAllText($env:DAMIR_PERSONAL_TEST_LOG, '')
        Push-Location -LiteralPath $directory
        try { $output = (& $pwsh -NoProfile -File (Join-Path $fixtureDir 'harness.ps1') 2>&1 | Out-String) }
        finally { Pop-Location }
        if ($LASTEXITCODE -eq 0 -or [IO.File]::ReadAllText($env:DAMIR_PERSONAL_TEST_LOG).Length -ne 0) { throw 'Unsafe workspace was not rejected before Docker.' }
    }
    Write-Host 'PASS: home folder and drive root are rejected before Docker'
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureDir)
    if (-not $resolvedFixture.StartsWith((Join-Path $repoDir '.uv-cache') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected cleanup path.' }
    if ([IO.Directory]::Exists($resolvedFixture)) { [IO.Directory]::Delete($resolvedFixture, $true) }
}
