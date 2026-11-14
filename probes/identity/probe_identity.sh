#!/usr/bin/env bash
# PROVIDER=postgres|mariadb|mysql bash probe_identity.sh: sign-in, tokens, sessions, bans, blocks, revocation and group sync
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/env.sh" || exit 1
export Accounts__refresh_reuse_leeway_seconds=0
export Accounts__providers__keycloak__group_sync__enabled=true Accounts__providers__keycloak__group_sync__interval_seconds=5
export Accounts__providers__keycloak__group_sync__client_id=genonline-sync Accounts__providers__keycloak__group_sync__client_secret=probe-sync-secret
stop_service
reset_db || exit 1
# an imported user that the Keycloak account with player_id 7001 signs in to
NOW=$(date -u +%Y-%m-%dT%H:%M:%S)
sql "INSERT INTO users (id, username, username_normalized, security_stamp, state, two_factor_enabled, failed_sign_in_count, created_at, updated_at) VALUES (7001,'CarolOld','CAROLOLD','s','active',false,0,'$NOW','$NOW')"
[ "$PROVIDER" = postgres ] && sql "SELECT setval(pg_get_serial_sequence('users','id'), 8000)" >/dev/null
start_service || exit 1
python -I "$HERE/probe_identity.py" $PROVIDER
rc=$?
stop_service
exit $rc
