# Client feature probes: run through probe_features.sh, which sets the environment and a fresh database (probes/README.md)
import os, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from rt import *
import rt

GAME, CHAT = "genonline_zerohour", "genonline_chat"
BIN = os.environ["BIN"]
KCADM = ["docker", "exec", os.environ["KC_CONTAINER"], "/opt/keycloak/bin/kcadm.sh"]
BOTH = ["observer", "stats_exporter"]
RELOAD_WAIT = 9  # the role cache reloads every 5 s


def connect(user, client=GAME):
    tok = login(user, client)
    if client == GAME:
        r = register(tok)
        assert r.status_code == 200, (r.status_code, r.text)
        h = Hub(tok)
    else:
        h = Hub(None, query="access_token=" + tok)
    assert h.status == 101, (user, h.status, h.body)
    h.token = tok
    h.ready = h.wait("Ready", 5)
    h.uid = h.ready["user"]["id"]
    return h


def until(predicate, seconds):
    """Polls the observable state instead of sleeping a guessed time; the last answer when it never holds"""
    end = time.time() + seconds
    while time.time() < end:
        if predicate():
            return True
        time.sleep(0.5)
    return predicate()


def code(res):
    return res.get("code") if isinstance(res, dict) and not res.get("ok") else ("ok" if isinstance(res, dict) and res.get("ok") else res)


def ok(res):
    return isinstance(res, dict) and res.get("ok") is True


def gos(*args):
    p = subprocess.run([BIN + "/GenOnlineService" + os.environ.get("EXE", ""), *map(str, args)], cwd=BIN, capture_output=True, text=True, timeout=120)
    return p.returncode, p.stdout + p.stderr


def kc(*args):
    return subprocess.run([*KCADM, *args], capture_output=True, text=True, timeout=60)


def kc_id(path, **query):
    q = [a for k, v in query.items() for a in ("-q", f"{k}={v}")]
    out = kc("get", path, "-r", "genonline", *q, "--fields", "id,name,username").stdout
    import json
    rows = json.loads(out or "[]")
    return rows[0]["id"] if rows else None


def features_update(hub, seconds=RELOAD_WAIT):
    ev = hub.wait("FeaturesUpdate", seconds)
    return ev and ev["features"]


def slot_of(state, uid):
    for s in state["lobby"]["slots"]:
        if s.get("member") and s["member"]["id"] == uid:
            return s
    return None


def latest_state(hub, uid, pred, seconds=3):
    st = hub.wait("LobbyState", seconds, pred=lambda s: slot_of(s, uid) is not None and pred(slot_of(s, uid)))
    return st and slot_of(st, uid)


# staff roles come from the Keycloak group sync, so the staff sign in first
def roles_loaded(token, role, seconds=30):
    """Waits until GET /api/v1/me shows the role: the role cache Ready.features is computed from has reloaded"""
    end = time.time() + seconds
    while time.time() < end:
        r = api("GET", "/api/v1/me", token)
        if r.status_code == 200 and role in r.json().get("roles", []):
            return True
        time.sleep(0.5)
    return False


staff = {user: login(user) for user in ("alice", "dave")}
check("admin role loaded from the sign-in", True, roles_loaded(staff["alice"], "admin"))
check("moderator role loaded from the sign-in", True, roles_loaded(staff["dave"], "moderator"))

bob = connect("bob")
check("player Ready features empty", [], bob.ready["features"])
check("player Ready permissions empty", [], bob.ready["permissions"])
alice = connect("alice")
check("admin Ready features", BOTH, alice.ready["features"])
dave = connect("dave")
check("moderator Ready features", BOTH, dave.ready["features"])

carol = connect("carol")
carol_chat = connect("carol", CHAT)
check("player chat Ready features empty", [], carol_chat.ready["features"])
erin = connect("erin")

# a CLI grant reaches the running service through the role reload
rc, out = gos("roles", "grant", carol.uid, "streamer", "--reason", "probe")
check("roles grant streamer", (0, True), (rc, "Granted streamer" in out))
check("grant sends FeaturesUpdate to the game connection", BOTH, features_update(carol))
check("grant sends FeaturesUpdate to the chat connection", BOTH, features_update(carol_chat, 3))
check("streamer keeps both connections", (None, None), (carol.closed, carol_chat.closed))
check("other players hear nothing", [], bob.take("FeaturesUpdate"))

rc, out = gos("roles", "grant", carol.uid, "streamer")
check("repeated grant changes nothing", (0, True), (rc, "nothing changed" in out))
rc, out = gos("roles", "grant", alice.uid, "streamer", "--reason", "probe")
check("grant streamer to admin", 0, rc)
check("unchanged list sends no event to the admin", None, features_update(alice))
check("unchanged list sends no event to the streamer", None, features_update(carol, 1))
rc, out = gos("roles", "list", carol.uid)
check("roles list shows streamer", True, "streamer" in out)
check("grant written to audit_log", "1", sql(f"SELECT COUNT(*) FROM audit_log WHERE target_id = {carol.uid} AND action = 'role_grant'"))

carol2 = connect("carol")
check("streamer Ready features", BOTH, carol2.ready["features"])
check("streamer Ready permissions empty", [], carol2.ready["permissions"])
check("streamer has no badge", [], carol2.ready["user"]["badges"])
carol = carol2  # the new game connection supersedes the old one

FORBID = lambda p: {"ok": False, "code": "forbidden", "params": {"permission": p}}
check("streamer cannot kick", FORBID("moderation.kick"), carol.act("Kick", {"user_id": bob.uid, "reason": "x"}))
check("streamer cannot mute", FORBID("moderation.mute"), carol.act("Mute", {"user_id": bob.uid, "duration_s": 60, "reason": "x"}))
check("moderator can mute streamer", "ok", code(dave.act("Mute", {"user_id": carol.uid, "duration_s": 60, "reason": "x"})))
check("moderator can unmute streamer", "ok", code(dave.act("Unmute", {"user_id": carol.uid, "reason": "x"})))

# observer seats follow the lobby's allow_observers for every player, no feature is checked
LOBBY = {"name": "observer probe", "map": {"name": "Tournament Desert", "path": "maps/tournament desert/tournament desert", "official": True}, "max_players": 4, "visibility": "public", "password": None, "preferred_port": 8088,
         "settings": {"starting_cash": 10000, "limit_superweapons": False, "vanilla_teams": False, "track_stats": True, "allow_observers": True, "max_camera_height": 310}}
CLOSED = dict(LOBBY, name="no observers", settings=dict(LOBBY["settings"], allow_observers=False))
INVALID = lambda f: {"ok": False, "code": "invalid_argument", "params": {"field": f}}
closed = carol.call("CreateLobby", CLOSED)
check("streamer creates a lobby without observers", True, ok(closed))
check("observer seat refused when the lobby allows none", INVALID("side"), carol.call("UpdateMySlot", {"side": -2}))
check("host cannot seat an observer when the lobby allows none", INVALID("slots.side"), carol.call("UpdateSlots", {"slots": [{"index": 0, "side": -2}]}))
check("streamer leaves the lobby without observers", True, ok(carol.call("LeaveLobby", {})))
carol.pump(0.5)
r = bob.call("CreateLobby", LOBBY)
check("CreateLobby ok", True, ok(r))
lobby_id = r["data"]["lobby_id"] if ok(r) else None
JOIN = {"lobby_id": lobby_id, "password": None, "preferred_port": 8089, "has_map": True}
check("player joins", True, ok(erin.call("JoinLobby", JOIN)))
check("streamer joins", True, ok(carol.call("JoinLobby", dict(JOIN, preferred_port=8090))))
bob.take("LobbyState")
check("plain player takes the observer seat", True, ok(erin.call("UpdateMySlot", {"side": -2})))
seat = latest_state(bob, erin.uid, lambda s: s["side"] == -2)
check("LobbyState shows the player in the observer seat", -2, seat and seat["side"])
erin_index = seat["index"] if seat else 1
check("side below the observer seat stays invalid", INVALID("side"), erin.call("UpdateMySlot", {"side": -3}))
check("player may pick a side again", True, ok(erin.call("UpdateMySlot", {"side": 2})))
bob.take("LobbyState")
check("host seats a plain player as observer", True, ok(bob.call("UpdateSlots", {"slots": [{"index": erin_index, "side": -2}]})))
seat = latest_state(bob, erin.uid, lambda s: s["side"] == -2)
check("LobbyState shows the host's observer seat", -2, seat and seat["side"])
check("streamer takes the observer seat", True, ok(carol.call("UpdateMySlot", {"side": -2})))

# a CLI revoke switches the features off without closing the sessions or the seat rules
rc, out = gos("roles", "revoke", carol.uid, "streamer", "--reason", "probe")
check("roles revoke streamer", (0, True), (rc, "Revoked streamer" in out))
check("revoke sends an empty FeaturesUpdate", [], features_update(carol))
check("revoke reaches the chat connection", [], features_update(carol_chat, 3))
check("revoke keeps the session", None, carol.closed)
check("observer seat needs no feature", True, ok(carol.call("UpdateMySlot", {"side": -2})))
bob.pump(0.5)
bob.take("LobbyState")
check("streamer leaves", True, ok(carol.call("LeaveLobby", {})))
check("streamer rejoins", True, ok(carol.call("JoinLobby", dict(JOIN, preferred_port=8090))))
seat = latest_state(bob, carol.uid, lambda s: True, 3)
check("remembered observer seat is kept where observers are allowed", -2, seat and seat["side"])
check("streamer leaves again", True, ok(carol.call("LeaveLobby", {})))
check("player leaves", True, ok(erin.call("LeaveLobby", {})))
erin.pump(0.5)
closed = erin.call("CreateLobby", dict(CLOSED, preferred_port=8091))
check("player creates a lobby without observers", True, ok(closed))
erin.take("LobbyState")
check("streamer joins the lobby without observers", True, ok(carol.call("JoinLobby", {"lobby_id": closed["data"]["lobby_id"] if ok(closed) else None, "password": None, "preferred_port": 8092, "has_map": True})))
seat = latest_state(erin, carol.uid, lambda s: True, 3)
check("remembered observer seat falls back to random without observers", -1, seat and seat["side"])

# Keycloak group sync maps genonline-streamer like the staff groups
gid = None
try:
    kc("config", "credentials", "--server", "http://localhost:8080", "--realm", "master", "--user", "admin", "--password", "admin")
    gid = kc_id("groups", search="genonline-streamer", exact="true")
    kid = kc_id("users", username="erin", exact="true")
    check("realm has genonline-streamer", True, gid is not None and kid is not None)
    erin.take("FeaturesUpdate")
    kc("update", f"users/{kid}/groups/{gid}", "-r", "genonline", "-s", f"realm=genonline", "-s", f"userId={kid}", "-s", f"groupId={gid}", "-n")
    STREAMER_ROW = f"SELECT COUNT(*) FROM user_roles WHERE user_id = {erin.uid} AND role = 'streamer' AND source = 'upstream:keycloak'"
    check("upstream streamer row", True, until(lambda: sql(STREAMER_ROW) == "1", 30))
    check("group sync grant sends FeaturesUpdate", BOTH, features_update(erin, 15))
    kc("delete", f"users/{kid}/groups/{gid}", "-r", "genonline")
    check("upstream streamer row removed", True, until(lambda: sql(STREAMER_ROW) == "0", 30))
    check("group sync removal sends an empty FeaturesUpdate", [], features_update(erin, 15))
    erin.pump(2)
    check("group sync removal keeps the session", None, erin.closed)
finally:
    if gid:
        kid = kc_id("users", username="erin", exact="true")
        kc("delete", f"users/{kid}/groups/{gid}", "-r", "genonline")

print(f"\n{rt.PASS} passed, {rt.FAIL} failed ({PROVIDER})")
sys.exit(1 if rt.FAIL else 0)
