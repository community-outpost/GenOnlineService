# Shared probe rig helpers, sourced by each suite's env.sh. See probes/README.md.
# Needs PROBE_ROOT: a scratch directory outside the repository for the build, logs and the realm copy.
if [ -z "$PROBE_ROOT" ]; then
  echo "Set PROBE_ROOT to a scratch directory outside the repository, for example D:/gb/probes or /tmp/genonline-probes" >&2
  return 1 2>/dev/null || exit 1
fi
PROBES=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$PROBES/.." && pwd)
# container names start with PROBE_NAME, so two rigs on one host never share a container
PROBE_NAME=${PROBE_NAME:-probe}
BIN=$PROBE_ROOT/bin
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) EXE=.exe; WINDOWS=1;; *) EXE=; WINDOWS=;; esac
export PROBE_ROOT PROBE_NAME BIN EXE
export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
# no __pycache__ next to the probes in the checkout
export PYTHONDONTWRITEBYTECODE=1

# A path docker can mount: D:/... in Git Bash, the path itself elsewhere
host_path() { if [ -n "$WINDOWS" ]; then (cd "$1" && pwd -W); else echo "$1"; fi; }

# Builds the service of this checkout into $BIN
build() {
  dotnet build "$(host_path "$REPO/GenOnlineService")/GenOnlineService.csproj" -p:Platform=x64 -o "$BIN" 2>&1 | grep -E " error |Build succeeded" | sort -u | tail -5
  # GET /api/v1/config needs the anti-cheat level file a deployment provides; it is not in the repository
  mkdir -p "$BIN/data"; [ -f "$BIN/data/anticheatconfig.dat" ] || echo 0 > "$BIN/data/anticheatconfig.dat"
}

gos() { (cd "$BIN" && "./GenOnlineService$EXE" "$@"); }

# The run's database: PROVIDER=postgres|mariadb|mysql, containers $PROBE_NAME-pg, -maria, -mysql
db_container() { case $PROVIDER in mariadb) echo "$PROBE_NAME-maria";; mysql) echo "$PROBE_NAME-mysql";; *) echo "$PROBE_NAME-pg";; esac; }
sql() {
  case $PROVIDER in
    mariadb) docker exec -i "$(db_container)" mariadb -uroot -pprobe genonline -N -e "$1";;
    mysql) docker exec -i "$(db_container)" mysql -uroot -pprobe genonline -N -e "$1" 2>/dev/null;;
    *) docker exec -i "$(db_container)" psql -U postgres -d genonline -tA -c "$1";;
  esac
}

# A fresh, migrated database for the run
reset_db() {
  case $PROVIDER in
    mariadb) docker exec "$(db_container)" mariadb -uroot -pprobe -e "DROP DATABASE IF EXISTS genonline; CREATE DATABASE genonline";;
    mysql) docker exec "$(db_container)" mysql -uroot -pprobe -e "DROP DATABASE IF EXISTS genonline; CREATE DATABASE genonline" 2>/dev/null;;
    *) docker exec "$(db_container)" psql -U postgres -c "DROP DATABASE IF EXISTS genonline WITH (FORCE)" -c "CREATE DATABASE genonline" >/dev/null;;
  esac
  gos migrate --yes > "$SP/migrate-$PROVIDER.log" 2>&1 || { echo "migrate failed"; tail -20 "$SP/migrate-$PROVIDER.log"; return 1; }
}

start_service() {
  (cd "$BIN" && "./GenOnlineService$EXE" > "$SP/service-$PROVIDER.log" 2>&1 &)
  local n
  for n in $(seq 1 60); do curl -sf "$BASE/-/readiness" > /dev/null && return 0; sleep 1; done
  echo "service did not start"; tail -30 "$SP/service-$PROVIDER.log"; return 1
}

# Stops only the service listening on this rig's port, never other GenOnlineService processes on the host
stop_service() {
  local pid
  if [ -n "$WINDOWS" ]; then
    for pid in $(netstat -ano | grep -E "127\.0\.0\.1:$SERVICE_PORT .*LISTENING" | awk '{print $NF}' | sort -u); do taskkill //F //PID "$pid" >/dev/null 2>&1; done
  else
    for pid in $(lsof -t -iTCP:"$SERVICE_PORT" -sTCP:LISTEN 2>/dev/null); do kill "$pid" 2>/dev/null; done
  fi
  sleep 1
}

# Copies a suite's realm with this rig's service port in its redirect URIs and starts Keycloak 26.8.1 on it
start_keycloak() { # container port realm-dir
  local dir="$PROBE_ROOT/kc-$1"
  mkdir -p "$dir"
  sed "s/@SERVICE_PORT@/$SERVICE_PORT/g" "$3/genonline-realm.json" > "$dir/genonline-realm.json"
  docker run -d --rm --name "$1" -p "$2:8080" -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
    -v "$(host_path "$dir"):/opt/keycloak/data/import" quay.io/keycloak/keycloak:26.8.1 start-dev --import-realm --hostname="http://127.0.0.1:$2" >/dev/null
}

wait_keycloak() { # port
  local n
  for n in $(seq 1 90); do curl -sf "http://127.0.0.1:$1/realms/genonline/.well-known/openid-configuration" > /dev/null && return 0; sleep 2; done
  echo "Keycloak on $1 did not come up"; return 1
}

PASS=0; FAIL=0
check() { # name expected actual
  if [ "$2" = "$3" ]; then PASS=$((PASS+1)); echo "PASS $1"; else FAIL=$((FAIL+1)); echo "FAIL $1: expected [$2] got [$3]"; fi
}
