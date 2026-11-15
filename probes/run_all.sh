#!/usr/bin/env bash
# Builds this checkout, recreates both rigs and runs every probe on every database it supports; exits non-zero on any failure
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/common.sh" || exit 1
build
bash "$HERE/identity/rig_up.sh" || exit 1
bash "$HERE/realtime/rig_up.sh" || exit 1
failed=0
run() { # suite probe provider
  local log="$PROBE_ROOT/run/$1-$2-$3.txt"
  PROVIDER=$3 bash "$HERE/$1/$2.sh" > "$log" 2>&1 || failed=1
  echo "$3 $2: $(grep '^== \|passed, ' "$log" | tr '\n' ' ')"
  grep '^FAIL\|Traceback' "$log"
}
for provider in postgres mariadb mysql; do
  run identity probe_identity $provider
  run identity probe_clients $provider
done
for provider in postgres mariadb; do
  run realtime probe_features $provider
done
exit $failed
