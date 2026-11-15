#!/usr/bin/env bash
# Recreates the identity rig's throwaway containers: Postgres 17, MariaDB 11.4, MySQL 8.4 and Keycloak 26.8.1 with the
# probe realm, then sets up the Keycloak users the way the GenOnline Keycloak plugin would (player ids, names and tags)
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
$K config credentials --server http://localhost:8080 --realm master --user admin --password admin >/dev/null 2>&1
# user profile attributes outside the declared profile (player_id, display_name, name_tag) are kept
docker exec $KC_CONTAINER sh -c '/opt/keycloak/bin/kcadm.sh get users/profile -r genonline > /tmp/up.json && sed -i "0,/{/s//{\"unmanagedAttributePolicy\":\"ENABLED\",/" /tmp/up.json && /opt/keycloak/bin/kcadm.sh update users/profile -r genonline -f /tmp/up.json'

# alice 7101, bob 7102, carol 7001 (imported), dave without a player id, erin claims bob's 7102, fay and gus share 7300
for u in dave erin fay gus; do
  $K get users -r genonline -q username=$u --fields id --format csv --noquotes | grep -q . || {
    $K create users -r genonline -s username=$u -s enabled=true -s firstName=$u -s lastName=P -s email=$u@example.org -s emailVerified=true >/dev/null
    $K set-password -r genonline --username $u --new-password $u-pw >/dev/null
  }
done
kcid() { $K get users -r genonline -q username=$1 --fields id --format csv --noquotes; }
setid() { docker exec $KC_CONTAINER sh -c "/opt/keycloak/bin/kcadm.sh update users/$(kcid $1) -r genonline -s 'attributes.player_id=[\"$2\"]' -s firstName=$1 -s lastName=P -s email=$1@example.org"; }
setid alice 7101; setid bob 7102; setid carol 7001; setid erin 7102; setid fay 7300; setid gus 7300
# names as the plugin issues them: the display_name and name_tag attributes travel as the name and name_tag claims
setname() { docker exec $KC_CONTAINER sh -c "/opt/keycloak/bin/kcadm.sh update users/$(kcid $1) -r genonline -s 'attributes.player_id=[\"$2\"]' -s 'attributes.display_name=[\"$3\"]' -s 'attributes.name_tag=[\"$4\"]'"; }
setname alice 7101 Alice 1001; setname bob 7102 Bob 1002; setname carol 7001 Carol 0042
# the plugin replaces the stock full name mapper, so the name claim is the display name and nothing else
PSID=$($K get client-scopes -r genonline --fields id,name --format csv --noquotes | grep ',profile$' | cut -d, -f1)
FNID=$($K get client-scopes/$PSID/protocol-mappers/models -r genonline --fields id,name --format csv --noquotes | grep ',full name$' | cut -d, -f1)
[ -n "$FNID" ] && $K delete client-scopes/$PSID/protocol-mappers/models/$FNID -r genonline

for n in $(seq 1 60); do
  docker exec $PROBE_NAME-mysql mysql -uroot -pprobe -e "select 1" >/dev/null 2>&1 && docker exec $PROBE_NAME-maria mariadb -uroot -pprobe -e "select 1" >/dev/null 2>&1 \
    && docker exec $PROBE_NAME-pg pg_isready -U postgres >/dev/null 2>&1 && { docker ps --format '{{.Names}} {{.Status}}' | grep "^$PROBE_NAME-"; exit 0; }
  sleep 2
done
echo "databases did not come up"; exit 1
