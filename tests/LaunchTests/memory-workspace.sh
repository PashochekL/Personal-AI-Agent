#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd -P)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/damir/config" "$fixture/bin" "$fixture/Project A" "$fixture/Project B"
# Windows checkouts can use CRLF; exercise the same shell program on Linux.
sed 's/\r$//' "$repo/damir" > "$fixture/damir/damir"
cp "$repo/config/litellm.yaml" "$fixture/damir/config/litellm.yaml"
cat > "$fixture/bin/docker" <<'MOCK'
#!/bin/sh
printf '%s|%s|%s\n' "$DAMIR_WORKSPACE" "$DAMIR_SESSION_ID" "$DAMIR_MEMORY_PROJECT" >> "$MEMORY_WORKSPACE_LOG"
printf '%s\n' "$*" >> "$MEMORY_CALL_LOG"
case "$*" in *'run -d'*) printf '%s\n' test-container;; esac
MOCK
chmod +x "$fixture/bin/docker"
export PATH="$fixture/bin:$PATH" DAMIR_TIMEZONE=UTC
export MEMORY_WORKSPACE_LOG="$fixture/workspace.log" MEMORY_CALL_LOG="$fixture/calls.log"
unset DAMIR_GITHUB_TOKEN DAMIR_MEMORY_PROJECT DAMIR_SESSION_ID
cd "$fixture/Project A"
sh "$fixture/damir/damir" run
first=$(head -n 1 "$MEMORY_WORKSPACE_LOG")
first_project=${first##*|}
grep -q 'compose up -d --wait memory-db embeddings' "$MEMORY_CALL_LOG"
grep -q 'compose build web-search' "$MEMORY_CALL_LOG"
grep -q 'compose up -d --wait gateway postgres web-search' "$MEMORY_CALL_LOG"
grep -q 'compose up -d --wait redis' "$MEMORY_CALL_LOG"
grep -q 'compose run -d --rm --no-deps --build --name .* backend' "$MEMORY_CALL_LOG"
grep -q 'compose run --no-deps --rm --build cli' "$MEMORY_CALL_LOG"
if grep -q -- '--build agent' "$MEMORY_CALL_LOG"; then exit 1; fi
grep -q 'stop --time 40 test-container' "$MEMORY_CALL_LOG"
: > "$MEMORY_WORKSPACE_LOG"
DAMIR_SESSION_ID=another-session sh "$fixture/damir/damir" run
second=$(head -n 1 "$MEMORY_WORKSPACE_LOG")
[ "${second##*|}" = "$first_project" ]
case "$second" in *'|another-session|'*) ;; *) exit 1;; esac
: > "$MEMORY_WORKSPACE_LOG"
cd "$fixture/Project B"
sh "$fixture/damir/damir" run
third=$(head -n 1 "$MEMORY_WORKSPACE_LOG")
[ "${third##*|}" != "$first_project" ]
: > "$MEMORY_CALL_LOG"
DAMIR_MEMORY_ENABLED=0 sh "$fixture/damir/damir" run
if grep -q 'memory-db embeddings' "$MEMORY_CALL_LOG"; then exit 1; fi
: > "$MEMORY_CALL_LOG"
DAMIR_MEMORY_ENABLED=0 sh "$fixture/damir/damir" backend
grep -q 'compose up -d --wait redis' "$MEMORY_CALL_LOG"
grep -q 'compose run --no-deps --rm --build --service-ports backend' "$MEMORY_CALL_LOG"
[ -s "$fixture/damir/.uv-cache/damir-backend-key" ]
backend_key=$(cat "$fixture/damir/.uv-cache/damir-backend-key")
DAMIR_MEMORY_ENABLED=0 sh "$fixture/damir/damir" backend
[ "$(cat "$fixture/damir/.uv-cache/damir-backend-key")" = "$backend_key" ]
printf '%s\n' 'Unix memory workspace checks passed'
