#!/bin/sh
set -eu
umask 077
run_id=$(openssl rand -hex 8)
router_name="damir-tg-router-$run_id"
bot_id=$(printf '%s' "$TELEGRAM_BOT_TOKEN" | cut -d: -f1 | sed 's/^0*//')
scope=$(printf '%s\000%s' "${DAMIR_MEMORY_OWNER:-local}" "$DAMIR_MEMORY_PROJECT" | openssl dgst -sha256 | awk '{print $NF}')
manifest="$PWD/.uv-cache/telegram-profiles-$run_id.json"
mkdir -p .uv-cache/telegram-keys
backends=
shells=
ipc_volumes=
cleanup_accounts() {
  docker stop --time 20 "$router_name" >/dev/null 2>&1 || true
  for name in $backends; do docker stop --time 40 "$name" >/dev/null 2>&1 || true; done
  for name in $shells; do docker stop --time 10 "$name" >/dev/null 2>&1 || true; done
  for volume in $ipc_volumes; do docker volume rm "$volume" >/dev/null 2>&1 || true; done
  rm -f -- "$manifest"
}
trap cleanup_accounts EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
docker compose build telegram-backend telegram-shell telegram
printf '[' > "$manifest"
separator=
old_ifs=$IFS
IFS=,
set -- $TELEGRAM_ALLOWED_USER_IDS
IFS=$old_ifs
for id in "$@"; do
  backend_name="damir-tg-$run_id-$id"
  shell_name="damir-tg-shell-$run_id-$id"
  key_file=".uv-cache/telegram-keys/$scope-$bot_id-$id.key"
  if [ ! -s "$key_file" ]; then openssl rand -hex 32 > "$key_file"; fi
  DAMIR_TELEGRAM_PROFILE_KEY=$(cat "$key_file")
  DAMIR_TELEGRAM_PROFILE_OWNER="telegram:$bot_id:$id"
  DAMIR_TELEGRAM_PROFILE_PROJECT="telegram-$scope"
  DAMIR_TELEGRAM_WORKSPACE_VOLUME="damir-tg-data-$scope-$bot_id-$id"
  DAMIR_TELEGRAM_IPC_VOLUME="damir-tg-ipc-$run_id-$id"
  export DAMIR_TELEGRAM_PROFILE_KEY DAMIR_TELEGRAM_PROFILE_OWNER DAMIR_TELEGRAM_PROFILE_PROJECT
  export DAMIR_TELEGRAM_WORKSPACE_VOLUME DAMIR_TELEGRAM_IPC_VOLUME
  ipc_volumes="$ipc_volumes $DAMIR_TELEGRAM_IPC_VOLUME"
  shells="$shells $shell_name"
  result=$(docker compose run -d --rm --no-deps --name "$shell_name" telegram-shell)
  [ -n "$result" ] || exit 1
  backends="$backends $backend_name"
  result=$(docker compose run -d --rm --no-deps --name "$backend_name" telegram-backend)
  [ -n "$result" ] || exit 1
  printf '%s{"user_id":%s,"url":"http://%s:8080/","api_key":"%s","owner_id":"%s","project_id":"%s"}' \
    "$separator" "$id" "$backend_name" "$DAMIR_TELEGRAM_PROFILE_KEY" "$DAMIR_TELEGRAM_PROFILE_OWNER" "$DAMIR_TELEGRAM_PROFILE_PROJECT" >> "$manifest"
  separator=,
done
printf ']\n' >> "$manifest"
DAMIR_TELEGRAM_BACKENDS_FILE=$manifest
export DAMIR_TELEGRAM_BACKENDS_FILE
printf '%s\n' 'Telegram: у каждого аккаунта отдельные файлы, память и диалоги. Остановка: Ctrl+C.'
docker compose run --no-deps --rm --name "$router_name" telegram
