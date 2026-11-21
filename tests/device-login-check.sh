#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_dir=$(mktemp -d)
test_dir=$(CDPATH= cd -- "$test_dir" && pwd -P)
trap 'rm -rf "$test_dir"' EXIT HUP INT TERM
cat > "$test_dir/docker" <<'MOCK'
#!/bin/sh
printf '%s\n' "$*" >> "$DAMIR_TEST_LOG"
test -n "${LITELLM_MASTER_KEY:-}"
printf '%s|%s|%s\n' "${DAMIR_WORKSPACE:-}" "${DAMIR_SESSION_ID:-}" "${DAMIR_SANDBOX_VOLUME:-}" >> "$DAMIR_TEST_ENV_LOG"
if [ "${DAMIR_TEST_FAIL:-}" = 1 ]; then printf '%s\n' 'docker test failure' >&2; exit 23; fi
case "$*" in 'compose run -d '*shell-sandbox*) printf '%s\n' 'test-sandbox-id';; esac
MOCK
chmod +x "$test_dir/docker"
export DAMIR_TEST_LOG="$test_dir/calls"
export DAMIR_TEST_ENV_LOG="$test_dir/environment"
PATH="$test_dir:$PATH" "$project_dir/damir" login >/dev/null
test "$(wc -l < "$DAMIR_TEST_LOG" | tr -d ' ')" = 2
grep -q -- '--user 0 --entrypoint python gateway' "$DAMIR_TEST_LOG"
grep -q -- 'Authenticator().get_access_token()' "$DAMIR_TEST_LOG"
: > "$DAMIR_TEST_LOG"
PATH="$test_dir:$PATH" "$project_dir/damir" run
grep -q -- 'compose up -d --wait gateway postgres' "$DAMIR_TEST_LOG"
grep -q -- 'compose build shell-sandbox' "$DAMIR_TEST_LOG"
grep -q -- 'compose run -d --rm --no-deps shell-sandbox' "$DAMIR_TEST_LOG"
grep -q -- 'compose run --no-deps --rm --build agent' "$DAMIR_TEST_LOG"
grep -q -- 'stop test-sandbox-id' "$DAMIR_TEST_LOG"
mkdir -p "$test_dir/Todo List" "$test_dir/Other Project"
: > "$DAMIR_TEST_ENV_LOG"
(cd "$test_dir/Todo List" && PATH="$test_dir:$PATH" "$project_dir/damir" run)
first=$(head -n 1 "$DAMIR_TEST_ENV_LOG")
case "$first" in "$test_dir/Todo List|workspace-"*'|damir-ipc-'*) ;; *) exit 1;; esac
: > "$DAMIR_TEST_ENV_LOG"
(cd "$test_dir/Other Project" && PATH="$test_dir:$PATH" "$project_dir/damir" run)
second=$(head -n 1 "$DAMIR_TEST_ENV_LOG")
test "$(printf '%s' "$first" | cut -d '|' -f 2)" != "$(printf '%s' "$second" | cut -d '|' -f 2)"
: > "$DAMIR_TEST_ENV_LOG"
(cd "$test_dir/Todo List" && PATH="$test_dir:$PATH" "$project_dir/damir" run)
again=$(head -n 1 "$DAMIR_TEST_ENV_LOG")
test "$(printf '%s' "$first" | cut -d '|' -f 2)" = "$(printf '%s' "$again" | cut -d '|' -f 2)"
install_dir="$test_dir/installed bin"
DAMIR_INSTALL_DIR="$install_dir" "$project_dir/install-damir" >/dev/null
DAMIR_INSTALL_DIR="$install_dir" "$project_dir/install-damir" >/dev/null
: > "$DAMIR_TEST_ENV_LOG"
(cd "$test_dir/Todo List" && PATH="$install_dir:$test_dir:$PATH" damir)
installed=$(head -n 1 "$DAMIR_TEST_ENV_LOG")
test "$(printf '%s' "$first" | cut -d '|' -f 2)" = "$(printf '%s' "$installed" | cut -d '|' -f 2)"
mkdir "$test_dir/collision"
printf 'keep' > "$test_dir/collision/damir"
if DAMIR_INSTALL_DIR="$test_dir/collision" "$project_dir/install-damir" >/dev/null 2>&1; then exit 1; fi
test "$(cat "$test_dir/collision/damir")" = keep
if DAMIR_TEST_FAIL=1 PATH="$test_dir:$PATH" "$project_dir/damir" run >"$test_dir/failure" 2>&1; then exit 1; else status=$?; fi
test "$status" = 23
grep -q 'docker test failure' "$test_dir/failure"
printf '%s\n' 'Device login and launch command checks passed'
