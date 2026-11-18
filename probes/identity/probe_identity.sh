#!/usr/bin/env bash
# PROVIDER=postgres|mariadb|mysql bash probe_identity.sh: sign-in, tokens, sessions, bans, blocks and revocation
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/env.sh" || exit 1
export Accounts__refresh_reuse_leeway_seconds=0
stop_service
reset_db || exit 1
# an imported user and its imported identity: carol's account at the test provider (its subject) signs in to user 7001
NOW=$(date -u +%Y-%m-%dT%H:%M:%S)
CAROL_SUB=$(MSYS_NO_PATHCONV=1 docker exec $KC_CONTAINER /opt/keycloak/bin/kcadm.sh get users -r genonline -q username=carol --fields id --format csv --noquotes | tr -d '\r')
[ -n "$CAROL_SUB" ] || { echo "carol is missing at the test provider"; exit 1; }
sql "INSERT INTO users (id, username, username_normalized, name_tag, name_key, security_stamp, state, two_factor_enabled, failed_sign_in_count, created_at, updated_at) VALUES (7001,'CarolOld','CAROLOLD',1,'carolold','s','active',false,0,'$NOW','$NOW')"
sql "INSERT INTO identities (user_id, provider, extern_uid, created_at) VALUES (7001,'idp','$CAROL_SUB','$NOW')"
[ "$PROVIDER" = postgres ] && sql "SELECT setval(pg_get_serial_sequence('users','id'), 8000)" >/dev/null
start_service || exit 1
python -I "$HERE/probe_identity.py" $PROVIDER
rc=$?
stop_service
exit $rc
