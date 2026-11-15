#!/usr/bin/env bash
# Recreates the realtime rig's throwaway containers: Keycloak 26.8.1 with the probe realm, Postgres 18 and MariaDB 11
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/env.sh" || exit 1
# docker takes container paths as they are
export MSYS_NO_PATHCONV=1
for c in $KC_CONTAINER $PROBE_NAME-pg $PROBE_NAME-maria; do docker rm -f "$c" >/dev/null 2>&1; done
start_keycloak $KC_CONTAINER $KC_PORT "$HERE/kc"
docker run -d --rm --name $PROBE_NAME-pg -p $PG_PORT:5432 -e POSTGRES_PASSWORD=probe postgres:18 >/dev/null
docker run -d --rm --name $PROBE_NAME-maria -p $MARIA_PORT:3306 -e MARIADB_ROOT_PASSWORD=probe mariadb:11 >/dev/null
wait_keycloak $KC_PORT || exit 1
for n in $(seq 1 90); do
  docker exec $PROBE_NAME-pg pg_isready -U postgres >/dev/null 2>&1 && docker exec $PROBE_NAME-maria mariadb -uroot -pprobe -e "SELECT 1" >/dev/null 2>&1 \
    && { docker ps --format '{{.Names}} {{.Status}}' | grep "^$PROBE_NAME-"; exit 0; }
  sleep 2
done
echo "databases did not come up"; exit 1
