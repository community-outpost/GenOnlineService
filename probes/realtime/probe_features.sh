#!/usr/bin/env bash
# PROVIDER=postgres|mariadb bash probe_features.sh: client features from roles, observer seats and the Keycloak group sync
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/env.sh" || exit 1
stop_service
reset_db || exit 1
start_service || exit 1
python -I "$HERE/probe_features.py"
rc=$?
stop_service
exit $rc
