# Realtime hub probe helpers: browser-flow login at the test OpenID Connect provider, session registration, raw SignalR JSON client
import base64, hashlib, html, json, os, re, secrets, socket, subprocess, sys, threading, time
import requests

BASE = os.environ["PROBE_BASE"]
HOST, PORT = "127.0.0.1", int(BASE.rsplit(":", 1)[1])
LOOPBACK = "http://127.0.0.1:54321/callback"
RS = b"\x1e"
PROVIDER = os.environ.get("PROVIDER", "postgres")
PASS = FAIL = 0


def check(name, expected, actual):
    global PASS, FAIL
    if expected == actual:
        PASS += 1
        print("PASS", name)
    else:
        FAIL += 1
        print(f"FAIL {name}: expected [{expected}] got [{actual}]")


def to_mariadb(statement):
    """Turns the Postgres forms the probes use into MariaDB ones"""
    statement = statement.replace("interval '31 days'", "INTERVAL 31 DAY")
    statement = re.sub(r"([\w.]+)::text", r"CAST(\1 AS CHAR)", statement)
    m = re.match(r"(?is)\s*SELECT\s+(.*?)\s+FROM\s+(.*)", statement)
    if m and " || " in m.group(1):
        parts = [x.strip() for x in m.group(1).split(" || ")]
        statement = "SELECT CONCAT(" + ", ".join(parts) + ") FROM " + m.group(2)
    return statement


def sql(statement):
    if PROVIDER != "postgres":
        statement = to_mariadb(statement)
    if PROVIDER == "postgres":
        cmd = ["docker", "exec", "-i", os.environ["PROBE_DB_CONTAINER"], "psql", "-U", "postgres", "-d", "genonline", "-tA", "-c", statement]
    else:
        cmd = ["docker", "exec", "-i", os.environ["PROBE_DB_CONTAINER"], "mariadb", "-uroot", "-pprobe", "genonline", "-N", "-e", statement]
    return subprocess.run(cmd, capture_output=True, text=True).stdout.strip()


def sign_in(username, password, client_id="genonline_zerohour", scope="openid offline_access game"):
    s = requests.Session()
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    params = {"client_id": client_id, "response_type": "code", "redirect_uri": LOOPBACK, "scope": scope, "state": "st8",
              "code_challenge": challenge, "code_challenge_method": "S256"}
    r = s.get(BASE + "/oauth/authorize", params=params, allow_redirects=False)
    for _ in range(20):
        for c in s.cookies:
            c.secure = False
        if r.status_code in (301, 302, 303):
            loc = r.headers["Location"]
            if loc.startswith(LOOPBACK):
                q = dict(x.split("=", 1) for x in loc.split("?", 1)[1].split("&"))
                return q.get("code"), verifier
            if loc.startswith("/"):
                loc = BASE + loc
            r = s.get(loc, allow_redirects=False)
            continue
        m = re.search(r'id="kc-form-login"[^>]*action="([^"]+)"', r.text)
        if r.status_code == 200 and m:
            r = s.post(html.unescape(m.group(1)), data={"username": username, "password": password, "credentialId": ""}, allow_redirects=False)
            continue
        raise RuntimeError(f"sign in stopped at {r.status_code}: {r.text[:300]}")
    raise RuntimeError("sign in loop")


def login(username, client_id="genonline_zerohour"):
    code, verifier = sign_in(username, username + "-pw", client_id)
    r = requests.post(BASE + "/oauth/token", data={"grant_type": "authorization_code", "code": code, "code_verifier": verifier,
                                                  "client_id": client_id, "redirect_uri": LOOPBACK})
    r.raise_for_status()
    return r.json()["access_token"]


def machine_token(account, scopes):
    """Mints a gopat- token for a new service account through the service CLI of the rig binary"""
    exe = os.path.join(os.environ["BIN"], "GenOnlineService" + os.environ.get("EXE", ""))
    cwd = os.path.dirname(exe)
    subprocess.run([exe, "service-accounts", "create", account], cwd=cwd, capture_output=True, text=True, timeout=60)
    for scope in scopes.split(","):
        subprocess.run([exe, "service-accounts", "grant", account, scope], cwd=cwd, capture_output=True, text=True, timeout=60)
    out = subprocess.run([exe, "tokens", "create", "--owner", "service:" + account, "--name", account, "--scopes", scopes, "--expires", time.strftime("%Y-%m-%d", time.gmtime(time.time() + 30 * 86400))],
                         cwd=cwd, capture_output=True, text=True, timeout=60)
    lines = [l.strip() for l in out.stdout.splitlines() if l.strip()]
    return lines[-1] if lines and lines[-1].startswith("gopat-") else (out.stdout + out.stderr)


def api(method, path, token, **kw):
    return requests.request(method, BASE + path, headers={"Authorization": "Bearer " + token}, **kw)


# HTTP status of each REST error code (protocol section 7), every other rule failure is 422
REST_STATUS = {"invalid_argument": 400, "unauthorized": 401, "forbidden": 403, "moderation.target_outranks": 403,
               "not_found": 404, "report.duplicate": 409, "rate_limited": 429, "name.unavailable": 503}


def rest(method, path, token, body=None, headers=None):
    """One call to /api/v1<path>: returns (status, parsed body or None, response)"""
    h = {"Authorization": "Bearer " + token}
    h.update(headers or {})
    r = requests.request(method, BASE + "/api/v1" + path, headers=h, json=body)
    try:
        data = r.json() if r.content else None
    except ValueError:
        data = None
    return r.status_code, data, r


def rest_result(name, status, data):
    """Turns a REST answer into the hub Result shape; a status that differs from section 7, or a body that is not
    {error, params}, counts as a failed check"""
    global FAIL
    if status in (200, 204):
        if status == 200 and not isinstance(data, dict):
            FAIL += 1
            print(f"FAIL {name}: 200 without a JSON object")
        return {"ok": True, "data": data if status == 200 else {}}
    if not isinstance(data, dict) or set(data) != {"error", "params"}:
        FAIL += 1
        print(f"FAIL {name}: error body is not {{error, params}}: {status} {data}")
        return {"ok": False, "code": f"http.{status}", "params": {}}
    expected = REST_STATUS.get(data["error"], 422)
    if status != expected:
        FAIL += 1
        print(f"FAIL {name}: {data['error']} answered {status}, section 7 says {expected}")
    return {"ok": False, "code": data["error"], "params": data["params"]}


def _sanction(method, a):
    body = {"type": method.lower()}
    body.update({k: a[k] for k in ("duration_s", "reason") if k in a})
    return "POST", f"/players/{a['user_id']}/sanctions", body


def _reason(a):
    return {"reason": a["reason"]} if "reason" in a else None


# The REST call that replaced each former hub method (protocol section 10), the argument is the old hub argument
REST_ROUTES = {
    "Kick": lambda a, me: _sanction("Kick", a),
    "Mute": lambda a, me: _sanction("Mute", a),
    "Ban": lambda a, me: _sanction("Ban", a),
    "DeleteMessage": lambda a, me: ("DELETE", f"/messages/{a['message_id']}", _reason(a)),
    "ReportMessage": lambda a, me: ("POST", "/reports", {k: a[k] for k in ("message_id", "user_id", "reason") if k in a}),
    "SetDisplayName": lambda a, me: ("PATCH", "/me", {"name": a["name"]}),
    "SendFriendRequest": lambda a, me: ("POST", "/me/friend_requests", {"user_id": a["user_id"]}),
    "AcceptFriendRequest": lambda a, me: ("POST", f"/me/friend_requests/{a['user_id']}/accept", None),
    "DeclineFriendRequest": lambda a, me: ("DELETE", f"/me/friend_requests/{a['user_id']}", None),
    "CancelFriendRequest": lambda a, me: ("DELETE", f"/me/friend_requests/{a['user_id']}", None),
    "RemoveFriend": lambda a, me: ("DELETE", f"/me/friends/{a['user_id']}", None),
    "BlockUser": lambda a, me: ("PUT", f"/me/blocks/{a['user_id']}", None),
    "UnblockUser": lambda a, me: ("DELETE", f"/me/blocks/{a['user_id']}", None),
    "CreateGroup": lambda a, me: ("POST", "/groups", {k: a[k] for k in ("user_ids", "name") if k in a}),
    "AddGroupMembers": lambda a, me: ("POST", f"/groups/{a['group_id']}/members", {"user_ids": a["user_ids"]}),
    "RemoveGroupMember": lambda a, me: ("DELETE", f"/groups/{a['group_id']}/members/{a['user_id']}", None),
    "LeaveGroup": lambda a, me: ("DELETE", f"/groups/{a['group_id']}/members/{me}", None),
    "RenameGroup": lambda a, me: ("PATCH", f"/groups/{a['group_id']}", {"name": a["name"]}),
    "GetPlaylists": lambda a, me: ("GET", "/matchmaking/playlists", None),
}


def act(token, me, method, arg):
    """Runs a former hub method over REST and answers in the hub Result shape; Unmute and Unban lift every active
    sanction of that kind, found through GET /api/v1/players/:id/sanctions?active=true"""
    if method in ("Unmute", "Unban"):
        kind = "mute" if method == "Unmute" else "ban"
        status, data, _ = rest("GET", f"/players/{arg['user_id']}/sanctions?active=true", token)
        listed = rest_result(method + " list", status, data)
        if not listed["ok"]:
            return listed
        ids = [s["id"] for s in data["sanctions"] if s["type"] == kind]
        if not ids:
            return {"ok": False, "code": "probe.no_active_sanction", "params": {}}
        result = None
        for sanction_id in ids:
            status, data, _ = rest("DELETE", f"/sanctions/{sanction_id}", token, _reason(arg))
            result = rest_result(method, status, data)
            if not result["ok"]:
                return result
        return result
    verb, path, body = REST_ROUTES[method](arg, me)
    status, data, _ = rest(verb, path, token, body)
    return rest_result(method, status, data)


def register(token, exe_crc=1234567890, ini_crc=987654321, anticheat_id=-1, game=True):
    body = {"machine_guid": "m-" + token[-6:], "mac_addr": "aa", "vol_serial": "v"}
    if game:
        body.update(exe_crc=exe_crc, ini_crc=ini_crc, anticheat_id=anticheat_id)
    return api("POST", "/api/v1/auth/session", token, json=body)


class Hub:
    """SignalR JSON hub protocol over a raw websocket: upgrade status, handshake, invocations, events, close record"""

    def __init__(self, token, path="/realtime", query=None, handshake=True):
        self.token = token
        self.send_lock = threading.Lock()
        self.last_send = time.time()
        self.closed = None
        self.sock = socket.create_connection((HOST, PORT), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        auth = f"Authorization: Bearer {token}\r\n" if token and not query else ""
        target = path + ("?" + query if query else "")
        self.sock.sendall((f"GET {target} HTTP/1.1\r\nHost: {HOST}:{PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n{auth}\r\n").encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                break
            buf += chunk
        head, _, self.rest = buf.partition(b"\r\n\r\n")
        self.status = int(head.split(b" ")[1]) if head else 0
        self.body = b""
        self.events = []
        self.closed = None
        self.next_id = 1
        self.text = b""
        self.completions = {}
        if self.status != 101:
            self.sock.settimeout(1)
            try:
                while True:
                    chunk = self.sock.recv(4096)
                    if not chunk:
                        break
                    self.rest += chunk
            except (socket.timeout, TimeoutError, ConnectionError):
                pass
            self.body = self.rest
            return
        if handshake:
            self._send_text(json.dumps({"protocol": "json", "version": 1}).encode() + RS)
            first = self._next_record(5)
            assert first == {}, first
            # the server drops a client silent for 30 s (protocol keep-alive row), so ping while the probe is busy elsewhere
            threading.Thread(target=self._keepalive, daemon=True).start()

    PING_EVERY = 10

    def _keepalive(self):
        while not self.closed:
            time.sleep(1)
            if time.time() - self.last_send >= self.PING_EVERY:
                try:
                    self._send_text(b'{"type":6}' + RS)
                except OSError:
                    return

    def _send_text(self, data, opcode=1):
        with self.send_lock:
            self.last_send = time.time()
            self._send_frame(data, opcode)

    def _send_frame(self, data, opcode):
        header = bytes([0x80 | opcode])
        n = len(data)
        if n < 126:
            header += bytes([0x80 | n])
        elif n < 65536:
            header += bytes([0x80 | 126]) + n.to_bytes(2, "big")
        else:
            header += bytes([0x80 | 127]) + n.to_bytes(8, "big")
        mask = os.urandom(4)
        self.sock.sendall(header + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))

    def _read(self, n):
        while len(self.rest) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("closed")
            self.rest += chunk
        data, self.rest = self.rest[:n], self.rest[n:]
        return data

    def _frame(self):
        b1, b2 = self._read(2)
        n = b2 & 0x7F
        if n == 126:
            n = int.from_bytes(self._read(2), "big")
        elif n == 127:
            n = int.from_bytes(self._read(8), "big")
        return b1 & 0x0F, self._read(n)

    def _next_record(self, seconds):
        end = time.time() + seconds
        while RS not in self.text:
            if time.time() > end:
                return None
            self.sock.settimeout(max(0.05, end - time.time()))
            try:
                op, payload = self._frame()
            except (socket.timeout, TimeoutError):
                return None
            except (ConnectionError, OSError):
                self.closed = self.closed or {"transport": "closed"}
                return None
            if op == 8:
                self.closed = self.closed or {"transport": "close frame"}
                return None
            if op in (1, 0):
                self.text += payload
        rec, _, self.text = self.text.partition(RS)
        return json.loads(rec)

    def pump(self, seconds=1.0, until=None):
        """Reads records for a while, collecting events; stops early when until(event) matches"""
        end = time.time() + seconds
        while time.time() < end:
            rec = self._next_record(end - time.time())
            if rec is None:
                if self.closed:
                    return None
                continue
            if rec.get("type") == 1:
                ev = (rec["target"], rec["arguments"][0] if rec.get("arguments") else None)
                self.events.append(ev)
                if until and until(ev):
                    return ev
            elif rec.get("type") == 7:
                self.closed = rec
                return None
            elif rec.get("type") == 3:
                self.completions[rec["invocationId"]] = rec
        return None

    completions = None

    def call(self, method, arg=None, seconds=5):
        if self.completions is None:
            self.completions = {}
        inv = str(self.next_id)
        self.next_id += 1
        self._send_text(json.dumps({"type": 1, "invocationId": inv, "target": method, "arguments": [arg if arg is not None else {}]}).encode() + RS)
        end = time.time() + seconds
        while time.time() < end and inv not in self.completions:
            rec = self._next_record(end - time.time())
            if rec is None:
                if self.closed:
                    break
                continue
            if rec.get("type") == 3:
                self.completions[rec["invocationId"]] = rec
            elif rec.get("type") == 1:
                self.events.append((rec["target"], rec["arguments"][0] if rec.get("arguments") else None))
            elif rec.get("type") == 7:
                self.closed = rec
        rec = self.completions.pop(inv, None)
        if rec is None:
            return {"timeout": True}
        if "error" in rec:
            return {"hub_error": rec["error"]}
        return rec.get("result")

    def act(self, method, arg=None):
        """A former hub method, now a REST call with this connection's token; events still arrive on the hub.
        Reads this connection briefly afterwards, as a hub call read every record up to its completion"""
        result = act(self.token, getattr(self, "uid", None), method, arg or {})
        if not self.closed:
            self.pump(0.3)
        return result

    def fire(self, method, arg):
        self._send_text(json.dumps({"type": 1, "target": method, "arguments": [arg]}).encode() + RS)

    def take(self, name):
        """Removes and returns the events with that name"""
        found = [a for n, a in self.events if n == name]
        self.events = [(n, a) for n, a in self.events if n != name]
        return found

    def wait(self, name, seconds=3, pred=None):
        for i, (n, a) in enumerate(self.events):
            if n == name and (pred is None or pred(a)):
                del self.events[i]
                return a
        ev = self.pump(seconds, until=lambda e: e[0] == name and (pred is None or pred(e[1])))
        if ev:
            self.events.remove(ev)
            return ev[1]
        return None

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass
