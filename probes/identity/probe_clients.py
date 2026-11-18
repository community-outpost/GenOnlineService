# Client registry probes: python -I probe_clients.py <provider>; run through probe_clients.sh, which sets the environment (probes/README.md)
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
        cmd = ["docker", "exec", "-i", DB, "psql", "-U", "postgres", "-d", "genonline", "-tA", "-F", "|", "-c", statement]
    elif PROVIDER == "mariadb":
        cmd = ["docker", "exec", "-i", DB, "mariadb", "-uroot", "-pprobe", "genonline", "-N", "-e", statement]
    else:
        cmd = ["docker", "exec", "-i", DB, "mysql", "-uroot", "-pprobe", "genonline", "-N", "-e", statement]
    return subprocess.run(cmd, capture_output=True, text=True).stdout.strip().replace("\t", "|")


def gos(*args):
    bin_dir = os.environ["BIN"]
    return subprocess.run([bin_dir + "/GenOnlineService" + os.environ.get("EXE", ""), *args], cwd=bin_dir, capture_output=True, text=True, env=os.environ)


def jwt_claims(token):
    p = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(p + "=" * (-len(p) % 4)))


class Browser:
    """One user agent: Keycloak sign-in, the service cookie and the consent page"""

    def __init__(self):
        self.s = requests.Session()
        self.page = None

    def authorize(self, username, password, client_id, scope="openid offline_access game", consent=None, prompt=None, redirect=LOOPBACK, token_override=None):
        verifier = secrets.token_urlsafe(48)
        challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
        params = {"client_id": client_id, "response_type": "code", "redirect_uri": redirect, "scope": scope, "state": "st8",
                  "code_challenge": challenge, "code_challenge_method": "S256"}
        if prompt:
            params["prompt"] = prompt
        r = self.s.get(BASE + "/oauth/authorize", params=params, allow_redirects=False)
        for _ in range(20):
            for c in self.s.cookies:
                c.secure = False
            if r.status_code in (301, 302, 303):
                loc = r.headers["Location"]
                if loc.startswith(redirect):
                    q = dict(x.split("=", 1) for x in loc.split("?", 1)[1].split("&"))
                    return ("error", q.get("error"), verifier) if "error" in q else ("code", q.get("code"), verifier)
                if loc.startswith("/"):
                    loc = BASE + loc
                r = self.s.get(loc, allow_redirects=False)
                continue
            m = re.search(r'id="kc-form-login"[^>]*action="([^"]+)"', r.text)
            if r.status_code == 200 and m:
                r = self.s.post(html.unescape(m.group(1)), data={"username": username, "password": password, "credentialId": ""}, allow_redirects=False)
                continue
            if r.status_code == 200 and 'name="consent"' in r.text:
                self.page = r
                if consent is None:
                    return ("consent", r.status_code, verifier)
                fields = {html.unescape(k): html.unescape(v) for k, v in re.findall(r'<input type="hidden" name="([^"]*)" value="([^"]*)">', r.text)}
                if token_override is not None:
                    fields["__go_consent_token"] = token_override
                fields["consent"] = consent
                r = self.s.post(BASE + "/oauth/authorize", data=fields, allow_redirects=False)
                consent = None
                continue
            return ("stop", r.status_code, verifier)
        return ("loop", None, verifier)


def exchange(code, verifier, client_id, redirect=LOOPBACK, secret=None):
    data = {"grant_type": "authorization_code", "code": code, "code_verifier": verifier, "client_id": client_id, "redirect_uri": redirect}
    if secret:
        data["client_secret"] = secret
    return requests.post(BASE + "/oauth/token", data=data)


def refresh(token, client_id):
    return requests.post(BASE + "/oauth/token", data={"grant_type": "refresh_token", "refresh_token": token, "client_id": client_id})


def login(username, password, client_id, scope="openid offline_access game"):
    kind, code, verifier = Browser().authorize(username, password, client_id, scope)
    assert kind == "code", (client_id, kind, code)
    return exchange(code, verifier, client_id).json()


def api(method, path, token, **kw):
    return requests.request(method, BASE + path, headers={"Authorization": "Bearer " + token}, **kw)


def register(token, exe=3735928559, ini=1234):
    return api("POST", "/api/v1/auth/session", token, json={"machine_guid": "m-" + token[-6:], "mac_addr": "aa", "vol_serial": "v", "exe_crc": exe, "ini_crc": ini, "anticheat_id": -1})


def until(predicate, seconds=15):
    end = time.time() + seconds
    while time.time() < end:
        value = predicate()
        if value:
            return value
        time.sleep(1)
    return predicate()


def ws_close(token, seconds=15):
    """Connects to /realtime and returns the close record's error code, or the HTTP status when the upgrade is refused"""
    key = base64.b64encode(os.urandom(16)).decode()
    sock = socket.create_connection(("127.0.0.1", PORT), timeout=seconds)
    sock.sendall((f"GET /realtime HTTP/1.1\r\nHost: 127.0.0.1:{PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                  f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nAuthorization: Bearer {token}\r\n\r\n").encode())
    head = b""
    while b"\r\n\r\n" not in head:
        head += sock.recv(1)
    status = int(head.split(b" ")[1])
    if status != 101:
        return status

    def send(text):
        payload = text.encode()
        mask = os.urandom(4)
        frame = bytes([0x81, 0x80 | len(payload)]) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        sock.sendall(frame)

    send('{"protocol":"json","version":1}\x1e')
    buf = b""
    end = time.time() + seconds
    while time.time() < end:
        try:
            chunk = sock.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        buf += chunk
        m = re.search(rb'\{"type":7[^\x1e]*\}', buf)
        if m:
            return json.loads(m.group(0)).get("error")
    return "open"


# --- seeding
listing = gos("clients", "list").stdout
rows = [l.split("\t") for l in listing.strip().splitlines()[1:]]
check("seed: four first-party clients", ["genonline_chat", "genonline_generals", "genonline_launcher", "genonline_zerohour"], [r[0] for r in rows])
check("seed: all enabled, first-party, public", True, all(r[1] == "enabled" and r[4] == "first" and r[5] == "public" for r in rows))
check("seed: kinds and games", [("chat", "none"), ("game", "generals"), ("launcher", "none"), ("game", "zerohour")], [(r[2], r[3]) for r in rows])
check("seed: genhub no longer seeded", "0", sql("SELECT count(*) FROM oauth_applications WHERE client_id='genhub'"))
check("seed: one audit row per seeded client", "4", sql("SELECT count(*) FROM audit_log WHERE action='client_add' AND source='config_seed'"))

# --- CLI
r = gos("clients", "add", "probe_app", "--kind", "app", "--name", "Probe <App>", "--redirect-uris", LOOPBACK, "--third-party", "true")
check("cli: add third-party app", 0, r.returncode)
DETAILS = "details::text" if PROVIDER == "postgres" else "CAST(details AS CHAR)"
check("cli: add writes audit", "client_add|cli", sql(f"SELECT action, source FROM audit_log WHERE target_kind='client' AND {DETAILS} LIKE '%probe_app%'"))
check("cli: duplicate refused", 2, gos("clients", "add", "probe_app", "--kind", "app", "--name", "x", "--redirect-uris", LOOPBACK, "--third-party", "true").returncode)
check("cli: bad client id refused", 2, gos("clients", "add", "Bad-Id", "--kind", "app", "--name", "x", "--redirect-uris", LOOPBACK, "--third-party", "true").returncode)
check("cli: app with game scope refused", 2, gos("clients", "add", "probe_x", "--kind", "app", "--name", "x", "--redirect-uris", LOOPBACK, "--third-party", "true", "--scopes", "openid,game").returncode)
check("cli: game client without a game refused", 2, gos("clients", "add", "probe_x", "--kind", "game", "--name", "x", "--redirect-uris", LOOPBACK, "--third-party", "true").returncode)
check("cli: unknown scope refused", 2, gos("clients", "add", "probe_x", "--kind", "app", "--name", "x", "--redirect-uris", LOOPBACK, "--third-party", "true", "--scopes", "openid,admin").returncode)
check("cli: plain http non-loopback redirect refused", 2, gos("clients", "add", "probe_x", "--kind", "app", "--name", "x", "--redirect-uris", "http://evil.example/cb", "--third-party", "true").returncode)
check("cli: add without --third-party refused", 2, gos("clients", "add", "probe_x", "--kind", "app", "--name", "x", "--redirect-uris", LOOPBACK).returncode)
check("cli: refused adds leave no rows", "0", sql("SELECT count(*) FROM clients WHERE client_id='probe_x'"))
r = gos("clients", "add", "probe_web", "--kind", "app", "--type", "confidential", "--name", "Probe Web", "--redirect-uris", LOOPBACK, "--third-party", "false")
secret = (re.search(r"Client secret \(shown once\): (\S+)", r.stdout) or [None, None])[1]
check("cli: confidential add prints a secret once", True, r.returncode == 0 and bool(secret))
stored = sql("SELECT client_secret FROM oauth_applications WHERE client_id='probe_web'")
check("cli: secret stored hashed", True, bool(stored) and secret not in stored)
check("cli: secret not in audit", "0", sql(f"SELECT count(*) FROM audit_log WHERE {DETAILS} LIKE '%{secret}%'"))
check("cli: kind is fixed on update", 2, gos("clients", "update", "probe_app", "--kind", "game").returncode)
audits = sql("SELECT count(*) FROM audit_log WHERE target_kind='client'")
check("cli: unchanged update writes nothing", (True, audits), ("unchanged" in gos("clients", "update", "probe_app").stdout, sql("SELECT count(*) FROM audit_log WHERE target_kind='client'")))
check("cli: update scopes of a public client beyond player scopes refused", 2, gos("clients", "update", "probe_app", "--scopes", "openid,admin").returncode)
time.sleep(6)

# --- consent for third-party clients
b = Browser()
kind, status, _ = b.authorize("alice", "alice-pw", "probe_app", scope="openid profile offline_access")
check("consent: third-party client shows the consent page", ("consent", 200), (kind, status))
check("consent: page names the app, encoded", True, "Probe &lt;App&gt;" in b.page.text and "<App>" not in b.page.text)
check("consent: page cannot be framed", ("DENY", "frame-ancestors 'none'"), (b.page.headers.get("X-Frame-Options"), b.page.headers.get("Content-Security-Policy")))
check("consent: prompt=none answers consent_required", ("error", "consent_required"), b.authorize("alice", "alice-pw", "probe_app", scope="openid", prompt="none")[:2])
check("consent: deny answers access_denied", ("error", "access_denied"), b.authorize("alice", "alice-pw", "probe_app", scope="openid", consent="deny")[:2])
check("consent: forged antiforgery token refused", ("stop", 400), b.authorize("alice", "alice-pw", "probe_app", scope="openid", consent="allow", token_override="forged")[:2])
kind, code, verifier = b.authorize("alice", "alice-pw", "probe_app", scope="openid profile offline_access", consent="allow")
check("consent: allow returns a code", "code", kind)
app_tokens = exchange(code, verifier, "probe_app").json()
check("consent: app token exchanged", True, "access_token" in app_tokens)
check("consent: app token opens no game session", 403, register(app_tokens["access_token"]).status_code)
check("consent: app may not ask for the game scope", True, b.authorize("alice", "alice-pw", "probe_app", scope="openid game", consent="allow")[:2] in (("error", "invalid_scope"), ("stop", 400)))
check("consent: first-party client skips the page", "code", Browser().authorize("alice", "alice-pw", "genonline_zerohour")[0])
kind, code, verifier = Browser().authorize("alice", "alice-pw", "probe_web", scope="openid", consent="allow")
check("confidential: code exchange without the secret refused", 401, exchange(code, verifier, "probe_web").status_code)
kind, code, verifier = Browser().authorize("alice", "alice-pw", "probe_web", scope="openid", consent="allow")
check("confidential: code exchange with the secret", 200, exchange(code, verifier, "probe_web", secret=secret).status_code)

# --- accepted builds
zh = login("bob", "bob-pw", "genonline_zerohour")
check("builds: no list accepts any build", 200, register(zh["access_token"]).status_code)
gos("clients", "update", "genonline_zerohour", "--builds", "1:2")
check("builds: unlisted build refused", 403, until(lambda: register(zh["access_token"]).status_code == 403 and 403))
check("builds: refusal body", {"error": "session.build_not_accepted", "params": {}}, register(zh["access_token"]).json())
gos("clients", "update", "genonline_zerohour", "--builds", "0xDEADBEEF:1234,1:2")
check("builds: listed build accepted", 200, until(lambda: register(zh["access_token"]).status_code == 200 and 200))
check("builds: list shows the builds", True, "0xDEADBEEF:0x000004D2" in gos("clients", "list").stdout)
gos("clients", "update", "genonline_zerohour", "--builds", "none")
check("builds: builds only for game clients", 2, gos("clients", "update", "genonline_chat", "--builds", "1:2").returncode)

# --- disable
chat = login("bob", "bob-pw", "genonline_chat")
check("disable: chat registers", 200, register(chat["access_token"]).status_code)
r = gos("clients", "disable", "genonline_chat", "--reason", "probe")
check("disable: cli exit 0", 0, r.returncode)
check("disable: audit with reason", "client_disable|probe", sql("SELECT action, reason FROM audit_log WHERE action='client_disable'"))
check("disable: refused body is the uniform 401", {"error": "unauthorized", "params": {}}, until(lambda: api("GET", "/api/v1/config", chat["access_token"]).status_code == 401 and api("GET", "/api/v1/config", chat["access_token"]).json()))
check("disable: access token refused at once after reload", 401, until(lambda: api("GET", "/api/v1/config", chat["access_token"]).status_code == 401 and 401))
check("disable: refresh refused", 400, refresh(chat["refresh_token"], "genonline_chat").status_code)
check("disable: authorizations revoked", "0", until(lambda: sql("SELECT count(*) FROM oauth_authorizations a JOIN oauth_applications p ON p.id = a.application_id WHERE p.client_id='genonline_chat' AND a.status='valid'") == "0" and "0"))
check("disable: authorize refused", True, Browser().authorize("bob", "bob-pw", "genonline_chat")[0] in ("stop", "error"))
check("disable: list shows disabled", True, "genonline_chat\tdisabled" in gos("clients", "list").stdout)
check("disable: second disable is a no-op", True, "unchanged" in gos("clients", "disable", "genonline_chat").stdout)
gos("clients", "update", "genonline_chat", "--enabled", "true")
check("enable: sign-in works again", "code", until(lambda: Browser().authorize("bob", "bob-pw", "genonline_chat")[0] == "code" and "code"))

# --- disable closes realtime
gen = login("carol", "carol-pw", "genonline_generals")
check("realtime: generals registers", 200, register(gen["access_token"]).status_code)
import threading
result = {}
t = threading.Thread(target=lambda: result.update(code=ws_close(gen["access_token"], 25)))
t.start()
time.sleep(2)
gos("clients", "disable", "genonline_generals")
t.join()
check("realtime: disabling closes the open connection with auth.revoked", "auth.revoked", result.get("code"))
check("realtime: disabled client's new connection refused", 401, ws_close(gen["access_token"], 3))
gos("clients", "update", "genonline_generals", "--enabled", "true")

# --- compatibility through the registry: any enabled client of the registry, by client id
def compat(client, version="2026.11.0"):
    r = requests.get(BASE + "/api/v1/compatibility", params={"client": client, "version": version})
    return r.status_code, r.json()

NO_CLIENT = (404, {"error": "not_found", "params": {"what": "client"}})
time.sleep(6)
check("compatibility: zerohour", (200, True), (lambda st, b: (st, b.get("compatible")))(*compat("genonline_zerohour")))
check("compatibility: generals", (200, True), (lambda st, b: (st, b.get("compatible")))(*compat("genonline_generals")))
check("compatibility: the retired generalsmd client is unknown", NO_CLIENT, compat("generalsmd"))
check("compatibility: a version that is not YYYY.MM.PATCH refused", (400, {"error": "invalid_argument", "params": {"field": "version"}}), compat("genonline_zerohour", "9.9.9"))
gos("clients", "disable", "genonline_generals")
check("compatibility: a disabled client is unknown", NO_CLIENT, until(lambda: compat("genonline_generals")[0] == 404 and compat("genonline_generals")))
gos("clients", "update", "genonline_generals", "--enabled", "true")

print(f"== clients {PROVIDER}: {PASS} passed, {FAIL} failed")
sys.exit(1 if FAIL else 0)
