#!/usr/bin/env bash
# PROVIDER=postgres|mariadb|mysql bash probe_clients.sh: seeded clients, CLI, consent, builds, disable, startup sweep
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/env.sh" || exit 1
stop_service
reset_db || exit 1
start_service || exit 1
python -I "$HERE/probe_clients.py" $PROVIDER
rc=$?

# a restart neither duplicates nor updates the seeded clients
stop_service
export Accounts__clients__0__display_name="Renamed by config"
start_service || exit 1
check "seed: restart keeps one row per client" 4 "$(sql "SELECT count(*) FROM clients WHERE client_id LIKE 'genonline_%'")"
check "seed: config does not update an existing client" "Generals Online (Zero Hour)" "$(sql "SELECT display_name FROM oauth_applications WHERE client_id='genonline_zerohour'")"
unset Accounts__clients__0__display_name

# startup sweep: a client disabled while the service was stopped loses its sign-ins at the next start
python -I - $PROVIDER "$HERE/probe_clients.py" "$SP/launcher_refresh.txt" <<'PY'
import sys
provider, probe, out = sys.argv[1:4]
sys.argv = ["probe_clients.py", provider]
exec(open(probe, encoding="utf-8").read().split("# --- seeding")[0])
open(out, "w").write(login("bob", "bob-pw", "genonline_launcher")["refresh_token"])
PY
stop_service
gos clients disable genonline_launcher --reason "while stopped" > /dev/null
start_service || exit 1
VALID="SELECT count(*) FROM oauth_authorizations a JOIN oauth_applications p ON p.id = a.application_id WHERE p.client_id='genonline_launcher' AND a.status='valid'"
for n in $(seq 1 15); do [ "$(sql "$VALID")" = "0" ] && break; sleep 1; done
check "sweep: authorizations of a client disabled while stopped revoked" 0 "$(sql "$VALID")"
check "sweep: audit row" 1 "$(sql "SELECT count(*) FROM audit_log WHERE action='revoke_client_sessions' AND source='startup_sweep'")"
check "sweep: refresh refused" 400 "$(curl -s -o "$SP/curl-body.txt" -w '%{http_code}' -X POST -d "grant_type=refresh_token&client_id=genonline_launcher&refresh_token=$(cat "$SP/launcher_refresh.txt")" $BASE/oauth/token)"
stop_service
echo "== clients shell $PROVIDER: $PASS passed, $FAIL failed"
[ $rc = 0 ] && [ $FAIL = 0 ]
