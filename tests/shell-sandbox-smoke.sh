#!/bin/sh
set -eu
sandbox_id=$(docker run -d --rm --network none --read-only --tmpfs /ipc:uid=65532,gid=65532 --tmpfs /workspace:uid=65532,gid=65532 --tmpfs /tmp:uid=65532,gid=65532 damir-persistent-shell-check)
trap 'docker stop "$sandbox_id" >/dev/null 2>&1 || true' EXIT
for attempt in 1 2 3 4 5 6 7 8 9 10; do
  if docker exec "$sandbox_id" test -S /ipc/sandbox.sock; then break; fi
done
docker exec -i -e HOME=/tmp "$sandbox_id" pwsh -NoLogo -NoProfile -NonInteractive -File /dev/stdin < tests/shell-sandbox-smoke.ps1
