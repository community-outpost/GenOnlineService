# Identity probe environment: source with PROVIDER=postgres|mariadb|mysql and PROBE_ROOT set (probes/README.md)
source "$(dirname "${BASH_SOURCE[0]}")/../common.sh" || return 1
SERVICE_PORT=${IDENTITY_SERVICE_PORT:-19041}
KC_PORT=${IDENTITY_KC_PORT:-18101}
PG_PORT=${IDENTITY_PG_PORT:-54401}; MARIA_PORT=${IDENTITY_MARIA_PORT:-33101}; MYSQL_PORT=${IDENTITY_MYSQL_PORT:-33102}
KC_CONTAINER=$PROBE_NAME-kc
SP=$PROBE_ROOT/run/identity; mkdir -p "$SP"
BASE=http://127.0.0.1:$SERVICE_PORT
export SP PROVIDER KC_CONTAINER PROBE_BASE=$BASE
case $PROVIDER in
  mariadb) export Database__db_provider=mysql Database__db_port=$MARIA_PORT Database__db_username=root;;
  mysql) export Database__db_provider=mysql Database__db_port=$MYSQL_PORT Database__db_username=root;;
  *) export Database__db_provider=postgres Database__db_port=$PG_PORT Database__db_username=postgres;;
esac
export PROBE_DB_CONTAINER=$(db_container)
export Database__db_host=127.0.0.1 Database__db_name=genonline Database__db_password=probe
export Kestrel__Endpoints__HTTPS__Url=http://127.0.0.1:$((SERVICE_PORT + 1)) Kestrel__Endpoints__HTTP__Url=$BASE
export JwtSettings__Key=probe-key-0123456789abcdef0123456789abcdef JwtSettings__Issuer=probe JwtSettings__Audience=probe
export Core__enforce_https=false Core__use_os_cert_store=true
export Devices__hmac_key_path=$SP/device_hmac.key
export Accounts__ephemeral_keys=true
# the throwaway Keycloak is only a generic OpenID Connect sign-in here, like any upstream provider
export Accounts__providers__idp__enabled=true Accounts__providers__idp__issuer=http://127.0.0.1:$KC_PORT/realms/genonline
export Accounts__providers__idp__client_id=genonline-dev Accounts__providers__idp__client_secret=probe-client-secret
export Accounts__providers__idp__only_provider=true
