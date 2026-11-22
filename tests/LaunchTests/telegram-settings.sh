#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd -P)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/project/scripts" "$fixture/project/config" "$fixture/project/.uv-cache" "$fixture/bin" "$fixture/keychain"
for script in telegram-settings telegram-isolated; do
  sed 's/\r$//' "$repo/scripts/$script.sh" > "$fixture/project/scripts/$script.sh"
done
sed 's/\r$//' "$repo/damir" > "$fixture/project/damir"
cp "$repo/config/litellm.yaml" "$fixture/project/config/litellm.yaml"
cat > "$fixture/bin/uname" <<'MOCK'
#!/bin/sh
printf '%s\n' "${TEST_PLATFORM:-Darwin}"
MOCK
cat > "$fixture/bin/git" <<'MOCK'
#!/bin/sh
set -eu
[ "$#" = 2 ] && [ "$1" = credential-osxkeychain ]
protocol= host= username= password=
while IFS= read -r line && [ -n "$line" ]; do
  case "$line" in
    protocol=*) protocol=${line#protocol=} ;;
    host=*) host=${line#host=} ;;
    username=*) username=${line#username=} ;;
    password=*) password=${line#password=} ;;
    *) exit 1 ;;
  esac
done
[ "$protocol" = https ] && [ "$host" = damir-telegram.local ]
case "$username" in damir-*) ;; *) exit 1 ;; esac
printf '%s:%s\n' "$2" "$username" >> "$TEST_STATE/keychain-calls"
entry="$TEST_STATE/keychain/$username"
case "$2" in
  get)
    if [ "${TEST_GET_FAIL:-0}" = 1 ]; then exit 1; fi
    if [ -f "$entry" ]; then printf 'password=%s\n' "$(cat "$entry")"; fi ;;
  store)
    if [ "${TEST_STORE_FAIL:-0}" = 1 ]; then exit 1; fi
    printf '%s\n' "$password" > "$entry" ;;
  *) exit 1 ;;
esac
MOCK
cat > "$fixture/bin/docker" <<'MOCK'
#!/bin/sh
printf '%s\n' "$*" >> "$TEST_STATE/docker-calls"
case "$*" in *'run -d'*) printf '%s\n' synthetic-container;; esac
MOCK
cat > "$fixture/project/settings-check.sh" <<'MOCK'
#!/bin/sh
set -eu
project_dir=$(pwd -P)
. ./scripts/telegram-settings.sh
damir_read_telegram_token() {
  [ "${TEST_ALLOW_INPUT:-0}" = 1 ] || { printf '%s\n' 'Unexpected token prompt' >&2; return 1; }
  printf '%s\n' token >> "$TEST_STATE/prompts"
  TELEGRAM_BOT_TOKEN=$TEST_INPUT_TOKEN
}
damir_read_telegram_ids() {
  [ "${TEST_ALLOW_INPUT:-0}" = 1 ] || { printf '%s\n' 'Unexpected ID prompt' >&2; return 1; }
  printf '%s\n' ids >> "$TEST_STATE/prompts"
  raw_ids=$TEST_INPUT_IDS
}
case "${1:-}" in
  personal-*) damir_personal_telegram "${1#personal-}" ;;
  *) damir_configure_telegram "$@" ;;
esac
MOCK
chmod +x "$fixture/bin/"*
export PATH="$fixture/bin:$PATH" TEST_STATE="$fixture" DAMIR_TIMEZONE=UTC DAMIR_MEMORY_ENABLED=0
export TEST_ALLOW_INPUT=1 TEST_INPUT_TOKEN='123456:synthetic_token_not_a_real_secret' TEST_INPUT_IDS='900001, 900002,900001'
unset TELEGRAM_BOT_TOKEN TELEGRAM_ALLOWED_USER_IDS TELEGRAM_ALLOWED_USER_ID DAMIR_GITHUB_TOKEN
cd "$fixture/project"
sh ./settings-check.sh telegram > "$fixture/output" 2>&1
[ "$(cat .uv-cache/telegram-users-123456.txt)" = '900001,900002' ]
[ "$(cat "$fixture/prompts")" = "$(printf 'token\nids')" ]
entry=$(find "$fixture/keychain" -type f)
[ "$(cat "$entry")" = "$TEST_INPUT_TOKEN" ]
# A restart must read Keychain, reuse the IDs, and never ask for input or store again.
TEST_ALLOW_INPUT=0 sh ./settings-check.sh telegram >> "$fixture/output" 2>&1
[ "$(grep -c '^store:' "$fixture/keychain-calls")" = 1 ]
if ! sh ./damir telegram-isolated </dev/null >> "$fixture/output" 2>&1; then cat "$fixture/output"; exit 1; fi
grep -q -- '--name damir-tg-.*-900001 telegram-backend' "$fixture/docker-calls"
grep -q -- '--name damir-tg-.*-900002 telegram-backend' "$fixture/docker-calls"
# Explicit setup overrides environment credentials and can switch to another bot.
export TELEGRAM_BOT_TOKEN='999999:synthetic_environment_token_only' TELEGRAM_ALLOWED_USER_IDS=900099
export TEST_INPUT_TOKEN='654321:synthetic_token_not_a_real_secret' TEST_INPUT_IDS=900003
sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1
[ "$(cat "$entry")" = "$TEST_INPUT_TOKEN" ]
[ "$(cat .uv-cache/telegram-users-654321.txt)" = 900003 ]
unset TELEGRAM_BOT_TOKEN TELEGRAM_ALLOWED_USER_IDS
TEST_INPUT_IDS='' sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1
[ "$(cat .uv-cache/telegram-users-654321.txt)" = 900003 ]
# Invalid replacements must leave both the token and the ID list intact.
cp "$entry" "$fixture/previous-token"
cp .uv-cache/telegram-users-654321.txt "$fixture/previous-ids"
if TEST_INPUT_TOKEN=invalid sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1; then exit 1; fi
if TEST_INPUT_IDS='900003,invalid' sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1; then exit 1; fi
cmp "$entry" "$fixture/previous-token"
cmp .uv-cache/telegram-users-654321.txt "$fixture/previous-ids"
# Store failure stops before replacing the ID list; read failure gives a recovery command.
if TEST_STORE_FAIL=1 TEST_INPUT_IDS=900004 sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1; then exit 1; fi
cmp "$entry" "$fixture/previous-token"
cmp .uv-cache/telegram-users-654321.txt "$fixture/previous-ids"
if TEST_GET_FAIL=1 TEST_ALLOW_INPUT=0 sh ./settings-check.sh telegram > "$fixture/read-error" 2>&1; then exit 1; fi
grep -q -- '--configure' "$fixture/read-error"
TEST_GET_FAIL=1 sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1
# Environment-only runs must bypass Keychain and must not persist their token.
cp "$fixture/keychain-calls" "$fixture/previous-calls"
TELEGRAM_BOT_TOKEN='999999:synthetic_environment_token_only' TELEGRAM_ALLOWED_USER_IDS=900099 TEST_GET_FAIL=1 TEST_ALLOW_INPUT=0 \
  sh ./settings-check.sh telegram >> "$fixture/output" 2>&1
cmp "$fixture/keychain-calls" "$fixture/previous-calls"
[ "$(cat "$entry")" = "$TEST_INPUT_TOKEN" ]
# Invalid saved IDs fail without silently prompting or broadening access.
printf '%s\n' invalid > .uv-cache/telegram-users-654321.txt
if TEST_ALLOW_INPUT=0 sh ./settings-check.sh telegram >> "$fixture/output" 2>&1; then exit 1; fi
sh ./settings-check.sh telegram --configure >> "$fixture/output" 2>&1
# The old delivery mode still requires explicit selection of its original owner.
if TEST_ALLOW_INPUT=0 sh ./settings-check.sh telegram-drain >> "$fixture/output" 2>&1; then exit 1; fi
if sh ./settings-check.sh telegram-drain --configure >> "$fixture/output" 2>&1; then exit 1; fi
# Another checkout must not accidentally reuse this checkout's bot token.
mkdir -p "$fixture/other/scripts" "$fixture/other/.uv-cache"
cp ./scripts/telegram-settings.sh "$fixture/other/scripts/telegram-settings.sh"
cp ./settings-check.sh "$fixture/other/settings-check.sh"
cd "$fixture/other"
if TEST_ALLOW_INPUT=0 sh ./settings-check.sh telegram >> "$fixture/output" 2>&1; then exit 1; fi
sh ./settings-check.sh telegram >> "$fixture/output" 2>&1
[ "$(find "$fixture/keychain" -type f | wc -l | tr -d ' ')" = 2 ]
# Linux keeps prompting for the token and reuses IDs, without writing plaintext credentials.
cd "$fixture/project"
cp "$fixture/keychain-calls" "$fixture/previous-calls"
export TEST_PLATFORM=Linux
sh ./settings-check.sh telegram >> "$fixture/output" 2>&1
cmp "$fixture/keychain-calls" "$fixture/previous-calls"
if find .uv-cache -type f -exec grep -l 'synthetic_token_not_a_real_secret\|synthetic_environment_token_only' {} + | grep -q .; then exit 1; fi
if grep -q 'synthetic_token_not_a_real_secret\|synthetic_environment_token_only' "$fixture/output" "$fixture/read-error" "$fixture/docker-calls" "$fixture/keychain-calls"; then exit 1; fi
# Personal /tg reuses the legacy Keychain entry without reading any user IDs.
export TEST_PLATFORM=Darwin
TEST_ALLOW_INPUT=0 sh ./settings-check.sh personal-load >> "$fixture/output" 2>&1
mkdir -p "$fixture/personal/scripts"
cp ./scripts/telegram-settings.sh "$fixture/personal/scripts/telegram-settings.sh"
cp ./settings-check.sh "$fixture/personal/settings-check.sh"
cd "$fixture/personal"
prompt_count=$(wc -l < "$fixture/prompts" | tr -d ' ')
unset TELEGRAM_BOT_TOKEN
sh ./settings-check.sh personal-setup >> "$fixture/output" 2>&1
[ "$(wc -l < "$fixture/prompts" | tr -d ' ')" -eq "$((prompt_count + 1))" ]
[ "$(tail -n 1 "$fixture/prompts")" = token ]
TEST_ALLOW_INPUT=0 sh ./settings-check.sh personal-load >> "$fixture/output" 2>&1
[ ! -d .uv-cache ]
[ "$(find "$fixture/keychain" -type f | wc -l | tr -d ' ')" = 3 ]
printf '%s\n' 'macOS Telegram settings checks passed (mock Keychain and Docker)'
