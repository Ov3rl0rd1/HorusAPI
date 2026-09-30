#!/usr/bin/env python3
"""
Horus monitoring gate — decides who may look at the monitoring box.

Caddy asks this service about every request to VictoriaMetrics (forward_auth →
GET /auth/check) and serves the login page from it under /auth/. Two ways in:

  * Telegram Mini App. The page opened from the bot's menu button receives
    initData signed with the bot token; only the user ids in TG_ALLOWED_USERS
    get a session. Re-entry is automatic: every launch brings fresh initData.
  * Access key. For an ordinary browser, a form; for scripts,
    `Authorization: Bearer <key>` on any request.

Standard library only: nothing to install, no image to build, and the whole
security-relevant surface fits on a couple of screens. It trusts
X-Forwarded-For, so it must only ever be reachable from Caddy on the compose
network — never publish its port.
"""

import base64
import hashlib
import hmac
import json
import logging
import os
import signal
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

log = logging.getLogger("gate")

HERE = Path(__file__).resolve().parent

# `__Host-` makes the browser refuse the cookie unless it is Secure, Path=/ and
# host-only — so nothing on a sibling subdomain can plant or overwrite it.
COOKIE = "__Host-horus_monitor"

# Where to land after logging in. The dashboards tab is what is worth seeing on a
# phone; everything else in vmui is one tap away.
DEFAULT_NEXT = "/vmui/#/dashboards"

TELEGRAM_API = "https://api.telegram.org"

MAX_BODY = 16 * 1024
MIN_KEY_LEN = 24
MIN_PROBE_LEN = 16


class ConfigError(Exception):
    pass


class AuthError(Exception):
    """A refused login. `code` goes back to the page, which holds the wording."""

    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


# ── Configuration ────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Config:
    domain: str
    bot_token: str
    tg_users: frozenset
    access_key: str
    probe_password: str
    tg_session_s: int
    key_session_s: int
    init_data_max_age_s: int
    menu_text: str
    session_salt: str
    fail_limit: int = 10
    fail_window_s: int = 15 * 60

    @property
    def origin(self) -> str:
        return f"https://{self.domain}"

    @property
    def tg_enabled(self) -> bool:
        return bool(self.bot_token and self.tg_users)

    @property
    def key_enabled(self) -> bool:
        return bool(self.access_key)

    @staticmethod
    def from_env(env=os.environ) -> "Config":
        def num(name, default):
            raw = env.get(name, "").strip()
            if not raw:
                return default
            try:
                value = int(raw)
            except ValueError:
                raise ConfigError(f"{name} must be a whole number, got {raw!r}")
            if value <= 0:
                raise ConfigError(f"{name} must be positive")
            return value

        domain = env.get("MONITOR_DOMAIN", "").strip().lower()
        if not domain or "/" in domain or ":" in domain:
            raise ConfigError("MONITOR_DOMAIN must be a bare host name, e.g. monitor.example.com")

        users = set()
        for part in env.get("TG_ALLOWED_USERS", "").replace(";", ",").split(","):
            part = part.strip()
            if not part:
                continue
            if not part.isdigit():
                # A negative number is a group chat id, not a person — the usual mix-up
                # with TELEGRAM_CHAT_ID, and one that would lock the owner out silently.
                raise ConfigError(f"TG_ALLOWED_USERS: {part!r} is not a Telegram user id (a positive number)")
            users.add(int(part))

        key = env.get("ACCESS_KEY", "").strip()
        if key and len(key) < MIN_KEY_LEN:
            raise ConfigError(f"ACCESS_KEY is too short: use at least {MIN_KEY_LEN} characters (openssl rand -hex 32)")

        probe = env.get("PROBE_PASSWORD", "").strip()
        if probe and len(probe) < MIN_PROBE_LEN:
            raise ConfigError(f"PROBE_PASSWORD is too short: use at least {MIN_PROBE_LEN} characters")

        cfg = Config(
            domain=domain,
            bot_token=env.get("TELEGRAM_BOT_TOKEN", "").strip(),
            tg_users=frozenset(users),
            access_key=key,
            probe_password=probe,
            tg_session_s=num("SESSION_HOURS_TG", 12) * 3600,
            key_session_s=num("SESSION_HOURS_KEY", 720) * 3600,
            init_data_max_age_s=num("TG_INIT_MAX_AGE", 3600),
            # Unset → the default label; explicitly empty → leave the bot's menu alone.
            menu_text=env.get("TG_MENU_BUTTON", "Мониторинг").strip(),
            session_salt=env.get("SESSION_SALT", ""),
        )

        if users and not cfg.bot_token:
            raise ConfigError("TG_ALLOWED_USERS is set but TELEGRAM_BOT_TOKEN is empty")
        if not cfg.tg_enabled and not cfg.key_enabled:
            raise ConfigError("Nobody could ever log in: set TG_ALLOWED_USERS and/or ACCESS_KEY")
        return cfg


# ── Sessions ─────────────────────────────────────────────────────────────────
# A session is a signed cookie, not a row anywhere: nothing to store, and it
# survives a restart. The signing key is DERIVED from secrets that already exist
# rather than being one more secret to generate and keep:
#
#   * the bot token — whoever holds it can forge Telegram initData anyway, so
#     keying sessions off it gives an attacker nothing new;
#   * the access key — rotating it logs every browser out, which is exactly what
#     you want after losing a device;
#   * SESSION_SALT — change it to log everyone out without rotating anything.

def session_key(cfg: Config) -> bytes:
    material = "\x00".join(["horus-monitor-session-v1", cfg.access_key, cfg.session_salt])
    return hmac.new(cfg.bot_token.encode(), material.encode(), hashlib.sha256).digest()


def _sign(key: bytes, payload: str) -> str:
    mac = hmac.new(key, payload.encode(), hashlib.sha256).digest()
    return base64.urlsafe_b64encode(mac).rstrip(b"=").decode()


def issue_session(cfg: Config, kind: str, subject: str, now: float) -> tuple:
    ttl = cfg.tg_session_s if kind == "tg" else cfg.key_session_s
    exp = int(now) + ttl
    payload = f"v1.{kind}.{subject}.{exp}"
    return f"{payload}.{_sign(session_key(cfg), payload)}", ttl


@dataclass(frozen=True)
class Session:
    kind: str
    subject: str
    exp: int


def verify_session(cfg: Config, value: str, now: float):
    """The session behind a cookie value, or None. Checked against the CURRENT
    configuration: removing someone from TG_ALLOWED_USERS, or clearing ACCESS_KEY,
    ends their sessions on the next request rather than when the cookie expires."""
    parts = (value or "").split(".")
    if len(parts) != 5 or parts[0] != "v1" or parts[1] not in ("tg", "key"):
        return None
    payload = ".".join(parts[:4])
    if not hmac.compare_digest(parts[4], _sign(session_key(cfg), payload)):
        return None
    try:
        exp = int(parts[3])
    except ValueError:
        return None
    if exp <= now:
        return None
    if parts[1] == "tg":
        if not parts[2].isdigit() or int(parts[2]) not in cfg.tg_users or not cfg.tg_enabled:
            return None
    elif not cfg.key_enabled:
        return None
    return Session(parts[1], parts[2], exp)


# ── Handoff: from Telegram Web's iframe to a tab of its own ──────────────────
# A browser that blocks third-party cookies (Chrome with that setting, any
# incognito window) keeps our partitioned cookie but also denies the iframe
# localStorage — and vmui touches localStorage unguarded, so it renders nothing.
# The gate cannot fix vmui; it can move the login out of the iframe. An embedded
# Telegram login also gets a one-time link, valid for a minute, that sets an
# ordinary first-party cookie when opened in a new tab.
#
# Single use is enforced in memory. A restart forgets which links were used, but
# every link is dead within HANDOFF_TTL_S anyway.

HANDOFF_TTL_S = 60


class Handoffs:
    def __init__(self):
        self._used = {}
        self._lock = threading.Lock()

    def issue(self, cfg: Config, uid: int, now: float) -> str:
        # "h1", not "v1": a handoff token can never pass for a session cookie, or
        # the other way round.
        payload = f"h1.{uid}.{int(now) + HANDOFF_TTL_S}.{base64.urlsafe_b64encode(os.urandom(12)).decode()}"
        return f"{payload}.{_sign(session_key(cfg), payload)}"

    def redeem(self, cfg: Config, token: str, now: float):
        """The Telegram user id behind a still-unused token, or None."""
        parts = (token or "").split(".")
        if len(parts) != 5 or parts[0] != "h1":
            return None
        payload = ".".join(parts[:4])
        if not hmac.compare_digest(parts[4], _sign(session_key(cfg), payload)):
            return None
        try:
            uid, exp = int(parts[1]), int(parts[2])
        except ValueError:
            return None
        if exp <= now or uid not in cfg.tg_users or not cfg.tg_enabled:
            return None
        with self._lock:
            for nonce in [n for n, e in self._used.items() if e <= now]:
                del self._used[nonce]
            if parts[3] in self._used:
                return None
            self._used[parts[3]] = exp
        return uid


def cookie_headers(value: str, max_age: int, embedded: bool) -> list:
    """Set-Cookie for a session.

    Opened as a top-level page (Telegram on a phone or desktop, any browser) the
    cookie is SameSite=Lax: another site cannot make the browser send it with a
    request of its own. Telegram Web, though, shows a Mini App in an iframe on
    web.telegram.org, where a Lax cookie is never sent at all — there it is
    SameSite=None and Partitioned, i.e. kept in a jar that belongs to that one
    embedding and is not sent anywhere else.
    """
    base = f"{COOKIE}={value}; Path=/; Max-Age={max_age}; Secure; HttpOnly"
    return [base + ("; SameSite=None; Partitioned" if embedded else "; SameSite=Lax")]


def clear_cookie_headers() -> list:
    # Both flavours: a partitioned cookie is only removed by a Set-Cookie that is
    # itself partitioned.
    return cookie_headers("", 0, embedded=False) + cookie_headers("", 0, embedded=True)


# ── Telegram Mini App initData ───────────────────────────────────────────────
# https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app
#
#   secret_key = HMAC_SHA256(key="WebAppData", msg=<bot token>)
#   hash       = hex(HMAC_SHA256(key=secret_key, msg=data_check_string))
#
# data_check_string is every received field except `hash`, sorted by key,
# `key=value` with the values URL-decoded, joined by "\n". NOTE the key order:
# this is not the Login Widget's SHA256(token), and mixing the two up is the
# classic way to accept nothing (or, with a bug the other way, anything).

def telegram_secret(bot_token: str) -> bytes:
    return hmac.new(b"WebAppData", bot_token.encode(), hashlib.sha256).digest()


def validate_init_data(init_data: str, bot_token: str, max_age_s: int, now: float) -> dict:
    """The verified `user` object, or AuthError."""
    if not init_data or len(init_data) > 8192:
        raise AuthError("invalid")
    try:
        pairs = urllib.parse.parse_qsl(init_data, keep_blank_values=True, strict_parsing=True)
    except ValueError:
        raise AuthError("invalid")

    fields = {}
    for key, value in pairs:
        if key in fields:
            raise AuthError("invalid")   # a repeated key makes the check-string ambiguous
        fields[key] = value

    received = fields.pop("hash", "")
    if len(received) != 64:
        raise AuthError("invalid")

    secret = telegram_secret(bot_token)

    def digest(items) -> str:
        check = "\n".join(f"{k}={v}" for k, v in sorted(items))
        return hmac.new(secret, check.encode(), hashlib.sha256).hexdigest()

    # Telegram's documentation computes `hash` over every field but itself, which
    # includes the newer Ed25519 `signature` field. Accept a check-string without it
    # as well: both are keyed by our bot token, so neither can be forged without it,
    # and a client that differs here should not lock the owner out.
    candidates = [list(fields.items())]
    if "signature" in fields:
        candidates.append([(k, v) for k, v in fields.items() if k != "signature"])
    if not any(hmac.compare_digest(digest(c), received.lower()) for c in candidates):
        raise AuthError("invalid")

    try:
        auth_date = int(fields.get("auth_date", ""))
    except ValueError:
        raise AuthError("invalid")
    age = now - auth_date
    if age > max_age_s:
        raise AuthError("expired")
    if age < -300:
        raise AuthError("invalid")        # signed "in the future": a clock that is badly off

    try:
        user = json.loads(fields.get("user", ""))
        int(user["id"])
    except (ValueError, KeyError, TypeError):
        raise AuthError("invalid")
    return user


# ── Brute-force brake ────────────────────────────────────────────────────────
# A random 64-character key cannot be guessed, but a short one typed by hand can,
# and every failed attempt is noise in the log. Ten failures per address in a
# quarter of an hour, then 429 until the window slides.

class Failures:
    def __init__(self, limit: int, window_s: int):
        self.limit, self.window_s = limit, window_s
        self._seen = {}
        self._lock = threading.Lock()

    def _recent(self, ip: str, now: float) -> list:
        stamps = [t for t in self._seen.get(ip, ()) if now - t < self.window_s]
        if stamps:
            self._seen[ip] = stamps
        else:
            self._seen.pop(ip, None)
        return stamps

    def retry_after(self, ip: str, now: float) -> int:
        """Seconds until this address may try again; 0 when it may try now."""
        with self._lock:
            stamps = self._recent(ip, now)
            if len(stamps) < self.limit:
                return 0
            return max(1, int(self.window_s - (now - stamps[-self.limit])) + 1)

    def record(self, ip: str, now: float) -> None:
        with self._lock:
            stamps = self._recent(ip, now)
            stamps.append(now)
            self._seen[ip] = stamps[-self.limit:]
            if len(self._seen) > 10_000:          # a spray of addresses must not grow this forever
                for stale in [k for k, v in self._seen.items() if now - v[-1] >= self.window_s]:
                    del self._seen[stale]


# ── Helpers ──────────────────────────────────────────────────────────────────

def safe_next(value) -> str:
    """Only a local path: an open redirect on a login page is a phishing kit."""
    if not value or not isinstance(value, str) or len(value) > 2048:
        return DEFAULT_NEXT
    if not value.startswith("/") or value.startswith("//") or value.startswith("/\\"):
        return DEFAULT_NEXT
    if any(ord(c) < 0x20 or c == "\\" for c in value):
        return DEFAULT_NEXT
    if value.startswith("/auth/") or value == "/auth":
        return DEFAULT_NEXT
    return value


def parse_cookies(header: str) -> dict:
    jar = {}
    for part in (header or "").split(";"):
        name, sep, value = part.strip().partition("=")
        if sep and name not in jar:
            jar[name] = value
    return jar


def keys_equal(given: str, expected: str) -> bool:
    # Compare digests, not the strings: compare_digest on unequal lengths returns
    # early, which would tell an attacker the key's length.
    return hmac.compare_digest(hashlib.sha256(given.encode()).digest(),
                               hashlib.sha256(expected.encode()).digest())


# ── Telegram menu button ─────────────────────────────────────────────────────
# Set per chat, for the allowed users only: nobody else even sees the button.
# Best effort — the gate works without it, and the same can be done by hand in
# @BotFather → Bot Settings → Menu Button.

def set_menu_buttons(cfg: Config, api: str = TELEGRAM_API, attempts: int = 5, pause_s: float = 10) -> dict:
    results = {}
    if not cfg.menu_text or not cfg.tg_enabled:
        return results
    button = json.dumps({"type": "web_app", "text": cfg.menu_text,
                         "web_app": {"url": f"{cfg.origin}/auth/"}}, ensure_ascii=False)
    for uid in sorted(cfg.tg_users):
        body = urllib.parse.urlencode({"chat_id": uid, "menu_button": button}).encode()
        for attempt in range(1, attempts + 1):
            try:
                req = urllib.request.Request(f"{api}/bot{cfg.bot_token}/setChatMenuButton", data=body)
                with urllib.request.urlopen(req, timeout=15) as resp:
                    json.load(resp)
                log.info("menu button set for Telegram user %s", uid)
                results[uid] = "ok"
                break
            except urllib.error.HTTPError as e:
                # A 4xx will not fix itself by retrying. The usual one: the user never
                # pressed Start in the bot, so there is no chat with them yet.
                try:
                    detail = json.load(e).get("description", "")
                except Exception:
                    detail = ""
                if 400 <= e.code < 500:
                    log.warning("menu button for %s refused (%s %s) — has this user pressed Start in the bot?",
                                uid, e.code, detail)
                    results[uid] = f"refused: {detail}"
                    break
                log.warning("menu button for %s: HTTP %s, attempt %s/%s", uid, e.code, attempt, attempts)
            except Exception as e:  # network down at boot: retry, never crash the gate
                # str(e) of a URLError names the host, never the path with the token in it.
                log.warning("menu button for %s: %s, attempt %s/%s", uid, e.__class__.__name__, attempt, attempts)
            if attempt < attempts:
                time.sleep(pause_s * attempt)
        else:
            results[uid] = "failed"
    return results


# ── HTTP ─────────────────────────────────────────────────────────────────────

LOGIN_CSP = ("default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
             "img-src 'self' data:; form-action 'self'; base-uri 'none'; "
             "frame-ancestors 'self' https://web.telegram.org")

STATIC = {
    "/auth/static/login.js": ("login.js", "text/javascript; charset=utf-8"),
    "/auth/static/login.css": ("login.css", "text/css; charset=utf-8"),
}


def make_handler(cfg: Config, failures: Failures, clock=time.time, handoffs=None):
    handoffs = handoffs or Handoffs()
    page = (HERE / "login.html").read_text(encoding="utf-8")
    page = (page.replace("__TG_LOGIN__", "1" if cfg.tg_enabled else "0")
                .replace("__KEY_LOGIN__", "1" if cfg.key_enabled else "0"))
    static = {path: ((HERE / "static" / name).read_bytes(), ctype) for path, (name, ctype) in STATIC.items()}

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"     # keep-alive with Caddy: /auth/check runs on every request
        server_version = "horus-gate"
        sys_version = ""
        timeout = 15

        # Per-request lines would log every vmui API call. Auth events are logged
        # explicitly instead, and never with a secret in them.
        def log_message(self, fmt, *args):
            pass

        # ── plumbing ──

        def client_ip(self) -> str:
            forwarded = self.headers.get("X-Forwarded-For", "")
            return forwarded.split(",")[0].strip() or self.client_address[0]

        def send(self, status, body=b"", ctype="text/plain; charset=utf-8", headers=(), cookies=()):
            if isinstance(body, str):
                body = body.encode()
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            if self.close_connection:
                self.send_header("Connection", "close")    # tell Caddy, not just hang up
            for name, value in headers:
                self.send_header(name, value)
            for cookie in cookies:
                self.send_header("Set-Cookie", cookie)
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def send_json(self, status, obj, headers=(), cookies=()):
            self.send(status, json.dumps(obj, ensure_ascii=False), "application/json; charset=utf-8",
                      headers, cookies)

        def redirect(self, location, status=303, cookies=()):
            self.send(status, headers=[("Location", location)], cookies=cookies)

        def read_form(self):
            """The urlencoded body, or None. Always leaves the connection in a state
            where the next request on it starts cleanly: Caddy keeps connections to
            the gate alive, and a body left unread would be parsed as the next request
            — somebody else's."""
            raw_length = self.headers.get("Content-Length")
            if raw_length is None:
                return {}
            try:
                length = int(raw_length)
            except ValueError:
                length = -1
            if length < 0 or length > MAX_BODY:
                self.close_connection = True
                return None
            raw = self.rfile.read(length).decode("utf-8", "replace")
            return dict(urllib.parse.parse_qsl(raw, keep_blank_values=True))

        def cross_origin(self) -> bool:
            # A login POST from another site is at best a mistake. Origin is absent
            # on some old clients; its absence is not treated as an attack.
            origin = self.headers.get("Origin")
            return bool(origin) and origin != "null" and origin != cfg.origin

        def session(self):
            value = parse_cookies(self.headers.get("Cookie", "")).get(COOKIE)
            return verify_session(cfg, value, clock()) if value else None

        # ── routes ──

        def do_HEAD(self):
            self.do_GET()

        def do_GET(self):
            path = urllib.parse.urlsplit(self.path).path
            if path == "/auth/check":
                return self.check()
            if path == "/auth/check-probe":
                return self.check_probe()
            if path in ("/auth", "/auth/"):
                return self.send(200, page, "text/html; charset=utf-8",
                                 headers=[("Content-Security-Policy", LOGIN_CSP),
                                          ("Referrer-Policy", "no-referrer")])
            if path in static:
                body, ctype = static[path]
                return self.send(200, body, ctype)
            if path == "/auth/whoami":
                s = self.session()
                if not s:
                    return self.send_json(401, {"error": "unauthorized"})
                return self.send_json(200, {"kind": s.kind, "user": s.subject, "expires": s.exp})
            if path == "/auth/handoff":
                return self.handoff()
            if path == "/auth/logout":
                log.info("logout from %s", self.client_ip())
                return self.redirect("/auth/?bye=1", cookies=clear_cookie_headers())
            return self.send(404, "not found")

        def do_POST(self):
            path = urllib.parse.urlsplit(self.path).path
            # Read the body before anything can answer early (see read_form).
            form = self.read_form()
            if form is None:
                return self.send(413, "request too large")
            if path == "/auth/telegram":
                return self.login_telegram(form)
            if path == "/auth/key":
                return self.login_key(form)
            return self.send(404, "not found")

        # forward_auth for everything VictoriaMetrics serves.
        def check(self):
            if self.session():
                return self.send(204)

            auth = self.headers.get("Authorization", "")
            if auth[:7].lower() == "bearer ":
                ip, now = self.client_ip(), clock()
                wait = failures.retry_after(ip, now)
                if wait:
                    return self.send_json(429, {"error": "too_many_attempts"}, headers=[("Retry-After", str(wait))])
                if cfg.key_enabled and keys_equal(auth[7:].strip(), cfg.access_key):
                    return self.send(204)
                failures.record(ip, now)
                log.warning("bad bearer key from %s", ip)
                return self.send_json(401, {"error": "unauthorized"})

            # A page load gets the login page; an API call from vmui gets a plain 401
            # rather than an HTML redirect it would try to parse as JSON.
            method = self.headers.get("X-Forwarded-Method", "GET").upper()
            wants_page = "text/html" in self.headers.get("Accept", "")
            if method in ("GET", "HEAD") and wants_page:
                target = safe_next(self.headers.get("X-Forwarded-Uri", "/"))
                return self.redirect("/auth/?next=" + urllib.parse.quote(target, safe=""), status=302)
            return self.send_json(401, {"error": "unauthorized", "login": "/auth/"})

        # forward_auth for the ru-probe's remote write. Basic auth, because that is
        # what vmagent speaks; the user name is not checked, only the password.
        def check_probe(self):
            if not cfg.probe_password:
                return self.send(404, "not found")          # route switched off
            ip, now = self.client_ip(), clock()
            wait = failures.retry_after(ip, now)
            if wait:
                return self.send(429, headers=[("Retry-After", str(wait))])
            auth = self.headers.get("Authorization", "")
            if not auth:
                # No credentials is not a guess — only wrong ones count towards the brake.
                return self.send(401, headers=[("WWW-Authenticate", 'Basic realm="horus-probe"')])
            password = None
            if auth[:6].lower() == "basic ":
                try:
                    password = base64.b64decode(auth[6:].strip(), validate=True).decode().partition(":")[2]
                except Exception:
                    password = None
            if password is not None and keys_equal(password, cfg.probe_password):
                return self.send(204)
            failures.record(ip, now)
            log.warning("bad probe credentials from %s", ip)
            return self.send(401, headers=[("WWW-Authenticate", 'Basic realm="horus-probe"')])

        def login_telegram(self, form):
            ip, now = self.client_ip(), clock()
            if self.cross_origin():
                return self.send_json(403, {"error": "origin"})
            if not cfg.tg_enabled:
                return self.send_json(403, {"error": "disabled"})
            wait = failures.retry_after(ip, now)
            if wait:
                return self.send_json(429, {"error": "slow", "retry": wait}, headers=[("Retry-After", str(wait))])
            try:
                user = validate_init_data(form.get("init_data", ""), cfg.bot_token, cfg.init_data_max_age_s, now)
            except AuthError as e:
                failures.record(ip, now)
                log.warning("telegram login refused from %s: %s", ip, e.code)
                return self.send_json(403, {"error": e.code})
            uid = int(user["id"])
            if uid not in cfg.tg_users:
                failures.record(ip, now)
                log.warning("telegram login refused from %s: user %s is not allowed", ip, uid)
                return self.send_json(403, {"error": "not_allowed"})
            value, ttl = issue_session(cfg, "tg", str(uid), now)
            embedded = form.get("embedded") == "1"
            log.info("telegram login: user %s (%s) from %s%s", uid, user.get("username", "-"), ip,
                     " [embedded]" if embedded else "")
            answer = {"ok": True}
            if embedded:
                answer["handoff"] = "/auth/handoff?t=" + urllib.parse.quote(handoffs.issue(cfg, uid, now), safe="")
            return self.send_json(200, answer, cookies=cookie_headers(value, ttl, embedded))

        # The one-time link from an embedded login, opened in a tab of its own.
        def handoff(self):
            ip, now = self.client_ip(), clock()
            query = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(self.path).query))
            uid = handoffs.redeem(cfg, query.get("t", ""), now)
            if uid is None:
                log.warning("dead handoff link from %s", ip)
                return self.redirect("/auth/?error=handoff")
            value, ttl = issue_session(cfg, "tg", str(uid), now)
            log.info("handoff: user %s moved out of the iframe, from %s", uid, ip)
            return self.redirect(DEFAULT_NEXT, cookies=cookie_headers(value, ttl, embedded=False))

        def login_key(self, form):
            ip, now = self.client_ip(), clock()
            back = "/auth/?next=" + urllib.parse.quote(safe_next(form.get("next")), safe="")
            if self.cross_origin():
                return self.send(403, "cross-origin login refused")
            if not cfg.key_enabled:
                return self.redirect(back + "&error=disabled")
            wait = failures.retry_after(ip, now)
            if wait:
                return self.redirect(back + f"&error=slow&retry={wait}")
            if not keys_equal(form.get("key", "").strip(), cfg.access_key):
                failures.record(ip, now)
                log.warning("bad access key from %s", ip)
                return self.redirect(back + "&error=key")
            value, ttl = issue_session(cfg, "key", "browser", now)
            embedded = form.get("embedded") == "1"
            log.info("key login from %s%s", ip, " [embedded]" if embedded else "")
            return self.redirect(safe_next(form.get("next")), cookies=cookie_headers(value, ttl, embedded))

    return Handler


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", stream=sys.stdout)
    try:
        cfg = Config.from_env()
    except ConfigError as e:
        log.error("configuration: %s", e)
        sys.exit(2)

    failures = Failures(cfg.fail_limit, cfg.fail_window_s)
    server = ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("GATE_PORT", "8080"))), make_handler(cfg, failures))
    server.daemon_threads = True

    # PID 1 in a container gets no default SIGTERM handling: without this,
    # `docker compose stop` waits its full ten seconds and then kills.
    signal.signal(signal.SIGTERM, lambda *_: threading.Thread(target=server.shutdown, daemon=True).start())

    threading.Thread(target=set_menu_buttons, args=(cfg,), daemon=True).start()

    log.info("gate for %s: telegram %s, access key %s, probe write %s",
             cfg.domain,
             f"on ({len(cfg.tg_users)} user(s))" if cfg.tg_enabled else "off",
             "on" if cfg.key_enabled else "off",
             "on" if cfg.probe_password else "off")
    server.serve_forever()


if __name__ == "__main__":
    main()
