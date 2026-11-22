# Sourced by damir. Keep credentials in the OS store, never in a plaintext file.
damir_read_telegram_token() {
  if [ ! -t 0 ]; then
    printf '%s\n' 'Задай TELEGRAM_BOT_TOKEN и TELEGRAM_ALLOWED_USER_IDS или запусти из терминала.' >&2
    return 1
  fi
  printf '%s' 'Токен Telegram-бота от BotFather (ввод скрыт): '
  trap 'stty echo 2>/dev/null || true; exit 130' INT
  trap 'stty echo 2>/dev/null || true; exit 143' TERM
  stty -echo
  IFS= read -r TELEGRAM_BOT_TOKEN || TELEGRAM_BOT_TOKEN=
  stty echo
  trap - INT TERM
  printf '\n'
}

damir_read_telegram_ids() {
  printf '%s' 'Разрешённые Telegram ID через запятую (Enter — сохранённый список, если есть): '
  IFS= read -r raw_ids || raw_ids=
}

damir_telegram_keychain() {
  # The helper consumes the password on stdin; it never appears in process arguments.
  {
    printf 'protocol=https\nhost=damir-telegram.local\nusername=%s\n' "$telegram_keychain_account"
    if [ "$1" = store ]; then printf 'password=%s\n' "$TELEGRAM_BOT_TOKEN"; fi
    printf '\n'
  } | git credential-osxkeychain "$1"
}

damir_configure_telegram() {
  telegram_command=$1
  configure_telegram=0
  case "${2:-}" in
    '') ;;
    --configure|-ConfigureTelegram)
      if [ "$telegram_command" != telegram ]; then
        printf '%s\n' 'Для изменения настроек используй damir telegram --configure.' >&2
        return 1
      fi
      configure_telegram=1 ;;
    *) printf '%s\n' 'Использование: damir telegram [--configure]' >&2; return 1 ;;
  esac
  telegram_platform=$(uname -s)
  save_telegram_token=0
  if [ "$telegram_platform" = Darwin ]; then
    # Each checkout keeps its own default bot, like the Windows local settings file.
    telegram_keychain_account="damir-$(printf '%s' "$project_dir" | openssl dgst -sha256 | awk '{print $NF}')"
  fi
  if [ "$configure_telegram" = 1 ]; then TELEGRAM_BOT_TOKEN=; fi
  if [ -z "${TELEGRAM_BOT_TOKEN:-}" ] && [ "$configure_telegram" = 0 ] && [ "$telegram_platform" = Darwin ]; then
    if telegram_credentials=$(damir_telegram_keychain get 2>/dev/null); then
      TELEGRAM_BOT_TOKEN=$(printf '%s\n' "$telegram_credentials" | sed -n 's/^password=//p')
      telegram_credentials=
    else
      printf '%s\n' 'Не удалось прочитать токен из Связки ключей. Проверь доступ к ней или выполни damir telegram --configure.' >&2
      return 1
    fi
  fi
  if [ -z "${TELEGRAM_BOT_TOKEN:-}" ]; then
    damir_read_telegram_token || return 1
    if [ "$telegram_platform" = Darwin ] && [ "$telegram_command" = telegram ]; then save_telegram_token=1; fi
  fi
  if ! printf '%s' "$TELEGRAM_BOT_TOKEN" | grep -Eq '^[0-9]{1,20}:[A-Za-z0-9_-]{16,235}$'; then
    printf '%s\n' 'Некорректный токен Telegram-бота. Для смены сохранённого токена: damir telegram --configure.' >&2
    return 1
  fi
  telegram_bot_number=$(printf '%s' "$TELEGRAM_BOT_TOKEN" | cut -d: -f1 | sed 's/^0*//')
  users_file=".uv-cache/telegram-users-$telegram_bot_number.txt"
  raw_ids=${TELEGRAM_ALLOWED_USER_IDS:-${TELEGRAM_ALLOWED_USER_ID:-}}
  if [ "$configure_telegram" = 1 ] || [ -z "$raw_ids" ]; then
    saved_ids=
    if [ "$telegram_command" = telegram ] && [ -f "$users_file" ]; then saved_ids=$(cat "$users_file"); fi
    if [ "$configure_telegram" = 0 ] && [ -n "$saved_ids" ]; then
      raw_ids=$saved_ids
    else
      damir_read_telegram_ids || return 1
      if [ -z "$raw_ids" ]; then raw_ids=$saved_ids; fi
    fi
  fi
  TELEGRAM_ALLOWED_USER_IDS=$(printf '%s\n' "$raw_ids" | awk '
    BEGIN { count=0 }
    { if (NR != 1) exit 1; n=split($0,a,","); if(n<1 || n>32) exit 1;
      for(i=1;i<=n;i++) { v=a[i]; gsub(/^[ \t]+|[ \t]+$/,"",v); if(v !~ /^[0-9]+$/) exit 1;
        sub(/^0+/,"",v); if(v=="" || length(v)>19 || (length(v)==19 && "x"v>"x9223372036854775807")) exit 1;
        if(!seen[v]++) { if(count++) printf ","; printf "%s",v; } } }
    END { if(count==0) exit 1 }
  ') || { printf '%s\n' 'Нужны положительные Telegram ID через запятую, не больше 32.' >&2; return 1; }
  TELEGRAM_ALLOWED_USER_ID=$TELEGRAM_ALLOWED_USER_IDS
  case "$TELEGRAM_ALLOWED_USER_IDS" in *,*) TELEGRAM_ALLOWED_USER_ID=;; esac
  if [ "$telegram_command" = telegram-drain ] && [ -z "$TELEGRAM_ALLOWED_USER_ID" ]; then
    printf '%s\n' 'Для старых отправок укажи один прежний ID.' >&2
    return 1
  fi
  if [ "$save_telegram_token" = 1 ]; then
    if ! damir_telegram_keychain store >/dev/null 2>&1; then
      printf '%s\n' 'Не удалось сохранить токен в Связке ключей. Проверь доступ к ней и повтори damir telegram --configure.' >&2
      return 1
    fi
  fi
  if [ "$telegram_command" = telegram ]; then printf '%s\n' "$TELEGRAM_ALLOWED_USER_IDS" > "$users_file"; fi
  export TELEGRAM_BOT_TOKEN TELEGRAM_ALLOWED_USER_ID TELEGRAM_ALLOWED_USER_IDS
}
