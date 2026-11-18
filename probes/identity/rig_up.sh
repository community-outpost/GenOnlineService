#!/usr/bin/env bash
# Recreates the identity rig's throwaway containers: Postgres 17, MariaDB 11.4, MySQL 8.4 and Keycloak 26.8.1 with the
# probe realm, a generic OpenID Connect provider for the browser sign-in (alice, bob and carol)
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/env.sh" || exit 1
# docker and kcadm take container paths as they are
export MSYS_NO_PATHCONV=1
for c in $PROBE_NAME-maria $PROBE_NAME-pg $PROBE_NAME-mysql $KC_CONTAINER; do docker rm -f "$c" >/dev/null 2>&1; done
docker run -d --rm --name $PROBE_NAME-maria -e MARIADB_ROOT_PASSWORD=probe -e MARIADB_DATABASE=genonline -p $MARIA_PORT:3306 mariadb:11.4 >/dev/null
docker run -d --rm --name $PROBE_NAME-pg -e POSTGRES_PASSWORD=probe -e POSTGRES_DB=genonline -p $PG_PORT:5432 postgres:17 >/dev/null
docker run -d --rm --name $PROBE_NAME-mysql -e MYSQL_ROOT_PASSWORD=probe -e MYSQL_DATABASE=genonline -p $MYSQL_PORT:3306 mysql:8.4 >/dev/null
start_keycloak $KC_CONTAINER $KC_PORT "$HERE/kc"
wait_keycloak $KC_PORT || exit 1
K="docker exec $KC_CONTAINER /opt/keycloak/bin/kcadm.sh"
$K config credentials --server http://localhost:8080 --realm master --user admin --password admin >/dev/null 2>&1  # probe_identity.sh reads carol's subject with it
for n in $(seq 1 60); do
  docker exec $PROBE_NAME-mysql mysql -uroot -pprobe -e "select 1" >/dev/null 2>&1 && docker exec $PROBE_NAME-maria mariadb -uroot -pprobe -e "select 1" >/dev/null 2>&1 \
    && docker exec $PROBE_NAME-pg pg_isready -U postgres >/dev/null 2>&1 && { docker ps --format '{{.Names}} {{.Status}}' | grep "^$PROBE_NAME-"; exit 0; }
  sleep 2
done
echo "databases did not come up"; exit 1
