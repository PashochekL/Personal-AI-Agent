#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd -P)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/project/config" "$fixture/project/scripts" "$fixture/bin"
sed 's/\r$//' "$repo/damir" > "$fixture/project/damir"
cp "$repo/config/litellm.yaml" "$fixture/project/config/litellm.yaml"
sed 's/\r$//' "$repo/scripts/telegram-isolated.sh" > "$fixture/project/scripts/telegram-isolated.sh"
sed 's/\r$//' "$repo/scripts/telegram-settings.sh" > "$fixture/project/scripts/telegram-settings.sh"
cat > "$fixture/bin/docker" <<'MOCK'
#!/bin/sh
printf '%s\n' "$*" >> "$TELEGRAM_LAUNCH_LOG"
case "$*" in *'run -d'*) printf '%s\n' synthetic-container;; esac
MOCK
chmod +x "$fixture/bin/docker"
export PATH="$fixture/bin:$PATH" DAMIR_TIMEZONE=UTC DAMIR_MEMORY_ENABLED=0
export TELEGRAM_LAUNCH_LOG="$fixture/calls"
export TELEGRAM_BOT_TOKEN='123456:synthetic_token_not_a_real_secret' TELEGRAM_ALLOWED_USER_ID=900001
export TELEGRAM_ALLOWED_USER_IDS='900001, 900002,900001'
unset DAMIR_GITHUB_TOKEN || true
cd "$fixture/project"
sh ./damir telegram-isolated > "$fixture/output"
grep -q -- '--name damir-tg-.*-900001 telegram-backend' "$TELEGRAM_LAUNCH_LOG"
grep -q -- '--name damir-tg-.*-900002 telegram-backend' "$TELEGRAM_LAUNCH_LOG"
grep -q 'compose run --no-deps --rm --name damir-tg-router-.* telegram' "$TELEGRAM_LAUNCH_LOG"
if grep -q -- '--service-ports' "$TELEGRAM_LAUNCH_LOG"; then exit 1; fi
if grep -q 'volume rm.*damir-tg-data\|volume rm.*damir_telegram_state' "$TELEGRAM_LAUNCH_LOG"; then exit 1; fi
if grep -q "$TELEGRAM_BOT_TOKEN" "$fixture/output"; then exit 1; fi
: > "$TELEGRAM_LAUNCH_LOG"
DAMIR_OBSERVABILITY_ENABLED=1 DAMIR_OTEL_ENDPOINT=http://otel-collector:4318 sh ./damir telegram-isolated > "$fixture/output"
grep -q 'compose --profile observability up -d otel-collector' "$TELEGRAM_LAUNCH_LOG"
: > "$TELEGRAM_LAUNCH_LOG"
DAMIR_OBSERVABILITY_ENABLED=1 DAMIR_OTEL_ENDPOINT= sh ./damir telegram-isolated > "$fixture/output"
if grep -q 'otel-collector' "$TELEGRAM_LAUNCH_LOG"; then exit 1; fi
: > "$TELEGRAM_LAUNCH_LOG"
TELEGRAM_ALLOWED_USER_ID=invalid
TELEGRAM_ALLOWED_USER_IDS='900001,invalid'
export TELEGRAM_ALLOWED_USER_ID TELEGRAM_ALLOWED_USER_IDS
if sh ./damir telegram-isolated > "$fixture/output" 2>&1; then exit 1; fi
[ ! -s "$TELEGRAM_LAUNCH_LOG" ]
printf '%s\n' 'Unix Telegram launch and owner validation checks passed'
