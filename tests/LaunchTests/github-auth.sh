#!/bin/sh
set -eu
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
mkdir -p "$test_dir/project/config" "$test_dir/bin"
cp damir "$test_dir/project/damir"
printf 'test\n' > "$test_dir/project/config/litellm.yaml"
cat > "$test_dir/bin/uname" <<'MOCK'
#!/bin/sh
printf 'Darwin\n'
MOCK
cat > "$test_dir/bin/stty" <<'MOCK'
#!/bin/sh
exit 0
MOCK
cat > "$test_dir/bin/git" <<'MOCK'
#!/bin/sh
case "$2" in
  get) [ -f "$TEST_STATE/credential" ] && cat "$TEST_STATE/credential" || true ;;
  store) if [ "${TEST_STORE_FAIL:-0}" = 1 ]; then cat >/dev/null; exit 1; fi; cat > "$TEST_STATE/credential" ;;
  erase) cat >/dev/null; rm -f "$TEST_STATE/credential" ;;
  *) exit 1 ;;
esac
MOCK
cat > "$test_dir/bin/docker" <<'MOCK'
#!/bin/sh
case "$*" in
  'compose run -d --rm --no-deps shell-sandbox') printf 'shell-id\n' ;;
  'compose run -d --rm --no-deps github-mcp') printf 'github-id\n'; printf 'github-started\n' >> "$TEST_STATE/events" ;;
  'compose run --no-deps --rm --build agent')
    printf 'agent:%s\n' "$DAMIR_GITHUB_MCP" >> "$TEST_STATE/events"
    code=$(head -n 1 "$TEST_STATE/codes")
    tail -n +2 "$TEST_STATE/codes" > "$TEST_STATE/codes.next"
    mv "$TEST_STATE/codes.next" "$TEST_STATE/codes"
    exit "$code" ;;
  *) exit 0 ;;
esac
MOCK
chmod +x "$test_dir/bin/"* "$test_dir/project/damir"
TEST_STATE="$test_dir"
PATH="$test_dir/bin:$PATH"
export TEST_STATE PATH
unset DAMIR_GITHUB_TOKEN || true
printf '71\n0\n' > "$test_dir/codes"
printf 'test-token-not-real\n' | "$test_dir/project/damir" > "$test_dir/output"
[ -s "$test_dir/credential" ]
[ "$(cat "$test_dir/events")" = "$(printf 'agent:0\ngithub-started\nagent:1')" ]
: > "$test_dir/events"
printf '72\n0\n' > "$test_dir/codes"
"$test_dir/project/damir" > "$test_dir/output"
[ ! -f "$test_dir/credential" ]
[ "$(cat "$test_dir/events")" = "$(printf 'github-started\nagent:1\nagent:0')" ]
if rg -q 'test-token-not-real' "$test_dir/output"; then exit 1; fi
 : > "$test_dir/events"
printf '71\n0\n' > "$test_dir/codes"
TEST_STORE_FAIL=1
export TEST_STORE_FAIL
printf 'test-token-not-real\n' | "$test_dir/project/damir" > "$test_dir/output" 2>/dev/null
[ ! -f "$test_dir/credential" ]
[ "$(cat "$test_dir/events")" = "$(printf 'agent:0\nagent:0')" ]
printf 'GitHub login, persistence and logout checks passed\n'
