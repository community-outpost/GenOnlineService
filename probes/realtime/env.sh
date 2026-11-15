# Realtime hub probe environment: source with PROVIDER=postgres|mariadb and PROBE_ROOT set (probes/README.md)
# The realm here issues no player_id claim, so the service runs self-hosted (player_ids off)
PROBE_NAME=${PROBE_NAME:-probe}-rt
source "$(dirname "${BASH_SOURCE[0]}")/../common.sh" || return 1
SERVICE_PORT=${REALTIME_SERVICE_PORT:-19051}
KC_PORT=${REALTIME_KC_PORT:-18111}
PG_PORT=${REALTIME_PG_PORT:-54411}; MARIA_PORT=${REALTIME_MARIA_PORT:-33111}
KC_CONTAINER=$PROBE_NAME-kc
SP=$PROBE_ROOT/run/realtime; mkdir -p "$SP"
BASE=http://127.0.0.1:$SERVICE_PORT
export SP PROVIDER KC_CONTAINER PROBE_BASE=$BASE
case $PROVIDER in
  mariadb) export Database__db_provider=mysql Database__db_port=$MARIA_PORT Database__db_username=root;;
  *) export Database__db_provider=postgres Database__db_port=$PG_PORT Database__db_username=postgres;;
esac
export PROBE_DB_CONTAINER=$(db_container)
export Database__db_host=127.0.0.1 Database__db_name=genonline Database__db_password=probe
export Kestrel__Endpoints__HTTPS__Url=http://127.0.0.1:$((SERVICE_PORT + 1)) Kestrel__Endpoints__HTTP__Url=$BASE
export JwtSettings__Key=probe-key-0123456789abcdef0123456789abcdef JwtSettings__Issuer=probe JwtSettings__Audience=probe
export Core__enforce_https=false Core__use_os_cert_store=true Core__reconnect_grace_period_ms=8000 Core__disable_full_mesh_check=true
export Devices__hmac_key_path=$SP/device_hmac.key
export RateLimiting__auth_limits_enabled=false
export Accounts__ephemeral_keys=true Accounts__refresh_reuse_leeway_seconds=0
export Accounts__providers__keycloak__enabled=true Accounts__providers__keycloak__issuer=http://127.0.0.1:$KC_PORT/realms/genonline
export Accounts__providers__keycloak__client_id=genonline-dev Accounts__providers__keycloak__client_secret=probe-client-secret
export Accounts__providers__keycloak__group_sync__enabled=true Accounts__providers__keycloak__group_sync__interval_seconds=5
export Accounts__providers__keycloak__group_sync__client_id=genonline-sync Accounts__providers__keycloak__group_sync__client_secret=probe-sync-secret
export Accounts__providers__keycloak__player_ids=false
export TURN__key=probe-turn-key TURN__token=probe-turn-token TURN__token_ttl=3600 TURN__automatic_token_invalidate=false
