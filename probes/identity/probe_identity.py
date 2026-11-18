# Identity phase 1 probes: python -I probe_identity.py <provider>; run through probe_identity.sh, which sets the environment (probes/README.md)
import base64, hashlib, html, json, os, re, secrets, socket, subprocess, sys, time
import requests

PROVIDER = sys.argv[1]
BASE = os.environ["PROBE_BASE"]
PORT = int(BASE.rsplit(":", 1)[1])
DB = os.environ["PROBE_DB_CONTAINER"]
LOOPBACK = "http://127.0.0.1:54321/callback"
PASS = FAIL = 0


def check(name, expected, actual):
    global PASS, FAIL
    if expected == actual:
        PASS += 1
        print("PASS", name)
    else:
        FAIL += 1
        print(f"FAIL {name}: expected [{expected}] got [{actual}]")


def sql(statement):
    if PROVIDER == "postgres":
        cmd = ["docker", "exec", "-i", DB, "psql", "-U", "postgres", "-d", "genonline", "-tA", "-c", statement]
    elif PROVIDER == "mariadb":
        cmd = ["docker", "exec", "-i", DB, "mariadb", "-uroot", "-pprobe", "genonline", "-N", "-e", statement]
    else:
        cmd = ["docker", "exec", "-i", DB, "mysql", "-uroot", "-pprobe", "genonline", "-N", "-e", statement]
    return subprocess.run(cmd, capture_output=True, text=True).stdout.strip()


def gos(*args):
    bin_dir = os.environ["BIN"]
    return subprocess.run([bin_dir + "/GenOnlineService" + os.environ.get("EXE", ""), *args], cwd=bin_dir, capture_output=True, text=True, env=os.environ)



def jwt_parts(token):
    def dec(p):
        return json.loads(base64.urlsafe_b64decode(p + "=" * (-len(p) % 4)))
    h, p, _ = token.split(".")
    return dec(h), dec(p)


def sign_in(username, password, client_id="genonline_zerohour", redirect=LOOPBACK, scope="openid offline_access game", pkce=True):
    """Runs the native app flow through the system browser stand-in; returns (status, code or final response)"""
    s = requests.Session()
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    params = {"client_id": client_id, "response_type": "code", "redirect_uri": redirect, "scope": scope, "state": "st8"}
    if pkce:
        params.update(code_challenge=challenge, code_challenge_method="S256")
    r = s.get(BASE + "/oauth/authorize", params=params, allow_redirects=False)
    for _ in range(20):
        # the test Keycloak runs on plain http but marks its cookies Secure; a browser on localhost still sends them
        for c in s.cookies:
            c.secure = False
        if r.status_code in (301, 302, 303):
            loc = r.headers["Location"]
            if loc.startswith(redirect.split("?")[0]) and redirect.startswith("http://127.0.0.1"):
                q = dict(x.split("=", 1) for x in loc.split("?", 1)[1].split("&"))
                return ("code", q.get("code"), verifier, q)
            if loc.startswith("/"):
                loc = BASE + loc
            r = s.get(loc, allow_redirects=False)
            continue
        m = re.search(r'id="kc-form-login"[^>]*action="([^"]+)"', r.text)
        if r.status_code == 200 and m:
            r = s.post(html.unescape(m.group(1)), data={"username": username, "password": password, "credentialId": ""}, allow_redirects=False)
            continue
        return ("stop", r.status_code, verifier, r.text[:2000])
    return ("loop", None, verifier, None)


def exchange(code, verifier, client_id="genonline_zerohour", redirect=LOOPBACK):
    return requests.post(BASE + "/oauth/token", data={"grant_type": "authorization_code", "code": code, "code_verifier": verifier, "client_id": client_id, "redirect_uri": redirect})


def refresh(token, client_id="genonline_zerohour"):
    return requests.post(BASE + "/oauth/token", data={"grant_type": "refresh_token", "refresh_token": token, "client_id": client_id})


def login(username, password):
    kind, code, verifier, _ = sign_in(username, password)
    assert kind == "code", (kind, code)
    t = exchange(code, verifier).json()
    return t


def api(method, path, token, **kw):
    return requests.request(method, BASE + path, headers={"Authorization": "Bearer " + token}, **kw)


def register(token):
    return api("POST", "/api/v1/auth/session", token, json={"machine_guid": "m-" + token[-6:], "mac_addr": "aa", "vol_serial": "v", "exe_crc": 3735928559, "ini_crc": 1234, "anticheat_id": -1})


def upgrade_body(token):
    """The JSON body of a refused realtime upgrade"""
    sock = socket.create_connection(("127.0.0.1", PORT), timeout=3)
    key = base64.b64encode(os.urandom(16)).decode()
    sock.sendall((f"GET /realtime HTTP/1.1\r\nHost: 127.0.0.1:{PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                  f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nAuthorization: Bearer {token}\r\n\r\n").encode())
    buf = b""
    try:
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            buf += chunk
            m = re.search(rb"\{.*\}", buf.partition(b"\r\n\r\n")[2], re.S)
            if m:
                return json.loads(m.group(0))
    except socket.timeout:
        pass
    return None


class WS:
    """Minimal SignalR JSON client for /realtime: handshake status, then hub records and the type 7 close record"""

    def __init__(self, token):
        self.sock = socket.create_connection(("127.0.0.1", PORT), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((f"GET /realtime HTTP/1.1\r\nHost: 127.0.0.1:{PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nAuthorization: Bearer {token}\r\n\r\n").encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                break
            buf += chunk
        head, _, self.rest = buf.partition(b"\r\n\r\n")
        self.status = int(head.split(b" ")[1]) if head else 0
        if self.status == 101:
            # SignalR handshake, one masked text frame
            data = b'{"protocol":"json","version":1}\x1e'
            mask = os.urandom(4)
            self.sock.sendall(bytes([0x81, 0x80 | len(data)]) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))
        self.body = self.rest

    def _read(self, n):
        while len(self.rest) < n:
            try:
                chunk = self.sock.recv(65536)
            except (socket.timeout, TimeoutError):
                if self.rest:
                    continue
                raise
            if not chunk:
                raise ConnectionError("closed")
            self.rest += chunk
        data, self.rest = self.rest[:n], self.rest[n:]
        return data

    def frames(self, seconds):
        """Yields (opcode, payload) until close or timeout"""
        end = time.time() + seconds
        self.sock.settimeout(1)
        while time.time() < end:
            try:
                b1, b2 = self._read(2)
            except (socket.timeout, TimeoutError):
                continue
            except ConnectionError:
                yield (8, b"")
                return
            n = b2 & 0x7F
            if n == 126:
                n = int.from_bytes(self._read(2), "big")
            elif n == 127:
                n = int.from_bytes(self._read(8), "big")
            try:
                payload = self._read(n)
            except ConnectionError:
                yield (8, b"")
                return
            yield (b1 & 0x0F, payload)
            if b1 & 0x0F == 8:
                return

    def close_code(self, seconds=15):
        """The error of the hub's type 7 close record, "socket-closed" without one, None on timeout"""
        for op, payload in self.frames(seconds):
            if op == 1:
                for rec in payload.split(b"\x1e"):
                    try:
                        msg = json.loads(rec)
                    except ValueError:
                        continue
                    if isinstance(msg, dict) and msg.get("type") == 7:
                        return msg.get("error") or "close-without-error"
            if op == 8:
                return "socket-closed"
        return None


# discovery and keys
conf = requests.get(BASE + "/.well-known/openid-configuration").json()
check("discovery authorize endpoint", BASE + "/oauth/authorize", conf.get("authorization_endpoint"))
check("discovery jwks endpoint", BASE + "/oauth/discovery/keys", conf.get("jwks_uri"))
check("discovery PKCE S256", True, "S256" in conf.get("code_challenge_methods_supported", []))
check("jwks has RSA key", "RSA", requests.get(conf["jwks_uri"]).json()["keys"][0]["kty"])

# one client id per application; the old names are gone
check("old client id refused", "stop", sign_in("alice", "alice-pw", client_id="gen_online_60hz")[0])
check("superseded client id refused", "stop", sign_in("alice", "alice-pw", client_id="genonline_game")[0])
check("previous client id generalsmd refused", "stop", sign_in("alice", "alice-pw", client_id="generalsmd")[0])
check("genonline_generals client id accepted", "code", sign_in("bob", "bob-pw", client_id="genonline_generals")[0])
for client, session_type in (("genonline_launcher", "GameLauncher"), ("genonline_chat", "ChatClient")):
    kind, code, verifier, _ = sign_in("bob", "bob-pw", client_id=client)
    t = exchange(code, verifier, client_id=client).json()
    r = api("POST", "/api/v1/auth/session", t["access_token"], json={})
    check(f"{client} registers a {session_type} session without build identity", (200, session_type), (r.status_code, r.json().get("session_type")))

# native sign-in, loopback on any port
alice = login("alice", "alice-pw")
hdr, claims = jwt_parts(alice["access_token"])
check("access token RS256", "RS256", hdr.get("alg"))
check("access token typ at+jwt", "at+jwt", hdr.get("typ"))
check("access token audience", "genonline-api", claims.get("aud") if isinstance(claims.get("aud"), str) else (claims.get("aud") or [None])[0])
check("access token carries no roles", False, any(k in claims for k in ("role", "roles", "groups")))
check("access token lifetime 15 min", 900, claims["exp"] - claims["iat"])
check("refresh token is a reference token", False, alice.get("refresh_token", "").count(".") == 2)
check("id token issued", True, "id_token" in alice)
ALICE_ID = int(claims["sub"])
check("a new user gets an id from the database, not the imported range", True, ALICE_ID > 7001)
check("alice takes the provider's username, the environment picks the tag", ("alice", True), (sql(f"SELECT username FROM users WHERE id={ALICE_ID}"), 1 <= int(sql(f"SELECT name_tag FROM users WHERE id={ALICE_ID}") or 0) <= 9999))
check("alice identity linked", "idp", sql(f"SELECT provider FROM identities WHERE user_id={ALICE_ID}"))
check("the provider's groups grant no role", "0", sql(f"SELECT count(*) FROM user_roles WHERE user_id={ALICE_ID}"))
gos("roles", "grant", str(ALICE_ID), "admin", "--reason", "probe")

kind, code, verifier, _ = sign_in("alice", "alice-pw", redirect="http://127.0.0.1:61000/other")
check("unregistered loopback path refused", "stop", kind)
kind, code, verifier, _ = sign_in("alice", "alice-pw", redirect="http://evil.example/callback")
check("non-loopback redirect refused", "stop", kind)
kind, code, verifier, q = sign_in("alice", "alice-pw", pkce=False)
check("authorize without PKCE refused", True, kind == "stop" or (q or {}).get("error") == "invalid_request")
kind, code, verifier, _ = sign_in("alice", "alice-pw")
check("code with wrong verifier refused", 400, exchange(code, "x" * 50).status_code)

# session registration and the game API
r = api("POST", "/api/v1/auth/session", alice["access_token"], json={"machine_guid": "m"})
check("game session without build identity 400", (400, {"error": "invalid_argument", "params": {"field": "exe_crc"}}), (r.status_code, r.json()))
r = register(alice["access_token"])
check("register session 200", 200, r.status_code)
check("register returns user id", ALICE_ID, r.json().get("user_id"))
check("game API with access token", 200, api("GET", "/api/v1/config", alice["access_token"]).status_code)
check("garbage bearer uniform 401", (401, {"error": "unauthorized", "params": {}}), (api("GET", "/api/v1/config", "abc.def.ghi").status_code, api("GET", "/api/v1/config", "abc.def.ghi").json()))
_, idt = jwt_parts(alice["id_token"])
check("id token refused by the API", 401, api("GET", "/api/v1/config", alice["id_token"]).status_code)
status = 0
for _ in range(20):
    status = api("GET", "/-/self_check/version", alice["access_token"]).status_code
    if status == 200:
        break
    time.sleep(1)
check("a CLI admin role grants server.stats within the reload tick", 200, status)
r = api("GET", "/api/v1/matches", alice["access_token"])
check("session principal lacks machine permission", (403, {"error": "forbidden", "params": {"permission": "match_history.read"}}), (r.status_code, r.json()))

# realtime: registration required, close on logout
bob = login("bob", "bob-pw")
BOB_ID = int(jwt_parts(bob["access_token"])[1]["sub"])
check("realtime without registration 409", 409, WS(bob["access_token"]).status)
check("register bob", 200, register(bob["access_token"]).status_code)
check("realtime after registration 101", 101, WS(bob["access_token"]).status)
bob2 = login("bob", "bob-pw")
check("realtime with another sign-in of the same user 409", 409, WS(bob2["access_token"]).status)

# refresh rotation and reuse
r1 = alice["refresh_token"]
rot = refresh(r1).json()
check("refresh returns new access token", True, "access_token" in rot)
check("refresh rotates the refresh token", True, rot.get("refresh_token") not in (None, r1))
check("rotated access token works", 200, api("GET", "/api/v1/config", rot["access_token"]).status_code)
time.sleep(2)
reuse = refresh(r1)
check("reused refresh token refused", (400, "invalid_grant"), (reuse.status_code, reuse.json().get("error")))
check("reuse revokes the newer token of the chain", 400, refresh(rot["refresh_token"]).status_code)

# logout drops realtime and the refresh chain
carl = login("alice", "alice-pw")
register(carl["access_token"])
ws = WS(carl["access_token"])
check("alice realtime connected", 101, ws.status)
check("logout 204", 204, api("DELETE", "/api/v1/auth/session", carl["access_token"]).status_code)
check("logout closes realtime with auth.revoked", "auth.revoked", ws.close_code())
check("logout revokes the refresh token", 400, refresh(carl["refresh_token"]).status_code)
check("re-register with a signed-out access token refused", 401, register(carl["access_token"]).status_code)
check("realtime after logout 409", 409, WS(carl["access_token"]).status)

# ban: sign-in allowed, game API 403, realtime closed, game refresh refused
ws = WS(bob["access_token"])
check("bob realtime connected", 101, ws.status)
gos("bans", "add", str(BOB_ID), "--reason", "probe ban")
check("ban closes realtime with moderation.banned", "moderation.banned", ws.close_code())
r = api("GET", "/api/v1/config", bob["access_token"])
BANNED = {"error": "moderation.banned", "params": {"reason": "probe ban"}}
check("banned: game API 403 with the error body, permanent ban has no expires_at", (403, BANNED), (r.status_code, r.json()))
check("banned: realtime upgrade 403", 403, WS(bob2["access_token"]).status)
check("banned: realtime upgrade body is the REST body", BANNED, upgrade_body(bob2["access_token"]))
check("banned: game refresh refused", 400, refresh(bob2["refresh_token"]).status_code)
kind, code, verifier, _ = sign_in("bob", "bob-pw")
check("banned: sign-in still works", "code", kind)
t = exchange(code, verifier).json()
check("banned: code exchange issues tokens for the appeal page", True, "access_token" in t)
check("banned: userinfo works", 200, requests.get(BASE + "/oauth/userinfo", headers={"Authorization": "Bearer " + t["access_token"]}).status_code)

# block: sign-in refused, everything refused
carol = login("carol", "carol-pw")
CAROL_ID = int(jwt_parts(carol["access_token"])[1]["sub"])
check("an imported identity signs in to the imported user", 7001, CAROL_ID)
check("the imported user keeps its name and tag", ("CarolOld", "1"), (sql("SELECT username FROM users WHERE id=7001"), sql("SELECT name_tag FROM users WHERE id=7001")))
check("the sign-in writes no name history", "0", sql("SELECT count(*) FROM user_username_history WHERE user_id=7001"))
register(carol["access_token"])
ws = WS(carol["access_token"])
sql("UPDATE users SET state='blocked' WHERE id=7001")
check("block closes realtime with auth.revoked", "auth.revoked", ws.close_code())
check("blocked: API 401", 401, api("GET", "/api/v1/config", carol["access_token"]).status_code)
check("blocked: refresh refused", 400, refresh(carol["refresh_token"]).status_code)
kind, status, _, body = sign_in("carol", "carol-pw")
check("blocked: sign-in refused", ("stop", 403), (kind, status))

# sign out everywhere through a privileged machine token
gos("service-accounts", "create", "website")
gos("service-accounts", "grant", "website", "users.revoke")
exp = time.strftime("%Y-%m-%d", time.gmtime(time.time() + 10 * 86400))
pat = gos("tokens", "create", "--owner", "service:website", "--name", "w", "--scopes", "users.revoke", "--expires", exp, "--allowed-ips", "127.0.0.1").stdout.strip().splitlines()[-1]
dave = login("alice", "alice-pw")
register(dave["access_token"])
ws = WS(dave["access_token"])
time.sleep(6)
check("revoke_sessions needs the permission", 403, api("POST", f"/api/v1/users/{ALICE_ID}/revoke_sessions", dave["access_token"]).status_code)
r = api("POST", f"/api/v1/users/{ALICE_ID}/revoke_sessions", pat)
check("revoke_sessions with gopat users.revoke", 200, r.status_code)
check("revoke_sessions closes realtime with auth.revoked", "auth.revoked", ws.close_code())
check("revoke_sessions revokes refresh", 400, refresh(dave["refresh_token"]).status_code)

# a temporary ban carries expires_at, ISO UTC with seconds; unknown users are not_found
gos("bans", "revoke", str(BOB_ID))
gos("bans", "add", str(BOB_ID), "--reason", "short", "--days", "1")
bob3 = login("bob", "bob-pw")
p = {}
for _ in range(10):
    r = api("GET", "/api/v1/config", bob3["access_token"])
    p = r.json().get("params", {}) if r.status_code == 403 else {}
    if p.get("reason") == "short":
        break
    time.sleep(1)
check("temporary ban: params reason and expires_at", ("short", True), (p.get("reason"), bool(re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", p.get("expires_at", "")))))
check("revoke_sessions of an unknown user 404 not_found", (404, {"error": "not_found", "params": {"what": "user"}}), (lambda r: (r.status_code, r.json()))(api("POST", "/api/v1/users/999999/revoke_sessions", pat)))

print(f"== identity {PROVIDER}: {PASS} passed, {FAIL} failed")
sys.exit(1 if FAIL else 0)
