#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT HUP INT TERM
cat > "$test_dir/docker" <<'MOCK'
#!/bin/sh
printf '%s\n' "$*" >> "$DAMIR_TEST_LOG"
test -n "${LITELLM_MASTER_KEY:-}"
MOCK
chmod +x "$test_dir/docker"
export DAMIR_TEST_LOG="$test_dir/calls"
PATH="$test_dir:$PATH" "$project_dir/damir" login >/dev/null
test "$(wc -l < "$DAMIR_TEST_LOG" | tr -d ' ')" = 2
grep -q -- '--user 0 --entrypoint python gateway' "$DAMIR_TEST_LOG"
grep -q -- 'Authenticator().get_access_token()' "$DAMIR_TEST_LOG"
: > "$DAMIR_TEST_LOG"
PATH="$test_dir:$PATH" "$project_dir/damir" run
grep -q -- 'compose up -d gateway' "$DAMIR_TEST_LOG"
grep -q -- 'compose run --rm --build agent' "$DAMIR_TEST_LOG"
printf '%s\n' 'Device login and launch command checks passed'
