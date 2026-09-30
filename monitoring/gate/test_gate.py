"""
Tests for the monitoring gate. Standard library only, like the gate itself:

    cd monitoring/gate && python3 -m unittest -v
    # or, without a local Python:
    docker run --rm -v "$PWD":/g -w /g python:3.13-alpine python -m unittest -v

The HTTP tests start the real handler on a free port and talk to it the way
Caddy does, so the status codes and headers checked here are the contract the
Caddyfile relies on.
"""

import hashlib
import hmac
import http.client
import json
import logging
import threading
import time
import unittest
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import gate

# The gate logs refusals on purpose; in a test run they are expected noise.
logging.getLogger("gate").setLevel(logging.CRITICAL)

TOKEN = "123456789:TEST-token-for-the-gate-tests_abcdefghij"
OWNER = 111
STRANGER = 222
KEY = "k" * 40
PROBE = "p" * 20
NOW = 1_800_000_000


def sign_init_data(fields: dict, token: str = TOKEN, skip_in_hash=()) -> str:
    """initData the way Telegram builds it: every field but `hash`, sorted, key=value
    joined by newlines, HMAC'd with HMAC("WebAppData", token)."""
    check = "\n".join(f"{k}={v}" for k, v in sorted(fields.items()) if k not in skip_in_hash)
    secret = hmac.new(b"WebAppData", token.encode(), hashlib.sha256).digest()
    digest = hmac.new(secret, check.encode(), hashlib.sha256).hexdigest()
    return urllib.parse.urlencode({**fields, "hash": digest})


def launch(user_id=OWNER, auth_date=NOW, **extra) -> dict:
    user = {"id": user_id, "first_name": "Имя", "username": "owner", "language_code": "ru"}
    return {"query_id": "AAHdF6IQAAAAAN0XohDhrOrc", "user": json.dumps(user, ensure_ascii=False),
            "auth_date": str(auth_date), **extra}


def config(**overrides) -> gate.Config:
    env = {"MONITOR_DOMAIN": "monitor.example.com", "TELEGRAM_BOT_TOKEN": TOKEN,
           "TG_ALLOWED_USERS": str(OWNER), "ACCESS_KEY": KEY, "PROBE_PASSWORD": PROBE}
    env.update(overrides)
    return gate.Config.from_env(env)


class InitDataTests(unittest.TestCase):

    def validate(self, init_data, token=TOKEN, now=NOW + 10):
        return gate.validate_init_data(init_data, token, 3600, now)

    def assertRefused(self, code, init_data, **kw):
        with self.assertRaises(gate.AuthError) as ctx:
            self.validate(init_data, **kw)
        self.assertEqual(code, ctx.exception.code)

    def test_genuine_data_yields_the_user(self):
        user = self.validate(sign_init_data(launch()))
        self.assertEqual(OWNER, user["id"])

    def test_a_different_bot_token_is_refused(self):
        self.assertRefused("invalid", sign_init_data(launch(), token="999:other-bot-token"))

    def test_an_edited_user_is_refused(self):
        forged = sign_init_data(launch(STRANGER)).replace(f"%22id%22%3A+{STRANGER}", f"%22id%22%3A+{OWNER}")
        self.assertNotEqual(forged, sign_init_data(launch(STRANGER)))
        self.assertRefused("invalid", forged)

    def test_the_login_widget_secret_is_not_accepted(self):
        # SHA256(token) is the Login Widget's key, not the Mini App's. A gate that mixed
        # them up would accept data signed for a different product.
        fields = launch()
        check = "\n".join(f"{k}={v}" for k, v in sorted(fields.items()))
        widget = hmac.new(hashlib.sha256(TOKEN.encode()).digest(), check.encode(), hashlib.sha256).hexdigest()
        self.assertRefused("invalid", urllib.parse.urlencode({**fields, "hash": widget}))

    def test_old_data_is_expired(self):
        self.assertRefused("expired", sign_init_data(launch(auth_date=NOW)), now=NOW + 3601)

    def test_data_from_the_future_is_refused(self):
        self.assertRefused("invalid", sign_init_data(launch(auth_date=NOW + 3600)), now=NOW)

    def test_a_repeated_field_is_refused(self):
        self.assertRefused("invalid", sign_init_data(launch()) + "&auth_date=" + str(NOW))

    def test_missing_hash_or_garbage_is_refused(self):
        self.assertRefused("invalid", urllib.parse.urlencode(launch()))
        self.assertRefused("invalid", "not a query string at all")
        self.assertRefused("invalid", "")

    def test_the_signature_field_is_covered_by_the_hash(self):
        fields = launch(signature="c2lnbmF0dXJl")
        self.assertEqual(OWNER, self.validate(sign_init_data(fields))["id"])

    def test_a_hash_that_leaves_out_signature_is_accepted_too(self):
        fields = launch(signature="c2lnbmF0dXJl")
        self.assertEqual(OWNER, self.validate(sign_init_data(fields, skip_in_hash=("signature",)))["id"])


class SessionTests(unittest.TestCase):

    def test_a_telegram_session_round_trips(self):
        cfg = config()
        value, ttl = gate.issue_session(cfg, "tg", str(OWNER), NOW)
        self.assertEqual(12 * 3600, ttl)
        self.assertEqual(gate.Session("tg", str(OWNER), NOW + ttl), gate.verify_session(cfg, value, NOW + 1))

    def test_expired_or_tampered_sessions_are_refused(self):
        cfg = config()
        value, ttl = gate.issue_session(cfg, "key", "browser", NOW)
        self.assertIsNone(gate.verify_session(cfg, value, NOW + ttl))
        self.assertIsNone(gate.verify_session(cfg, value.replace(".key.", ".tg."), NOW))
        self.assertIsNone(gate.verify_session(cfg, value[:-2] + "AA", NOW))
        self.assertIsNone(gate.verify_session(cfg, "v1.tg.111.99999999999", NOW))
        self.assertIsNone(gate.verify_session(cfg, "", NOW))

    def test_removing_a_user_ends_their_session_at_once(self):
        value, _ = gate.issue_session(config(), "tg", str(OWNER), NOW)
        self.assertIsNone(gate.verify_session(config(TG_ALLOWED_USERS="333"), value, NOW))

    def test_rotating_the_key_or_the_salt_logs_everyone_out(self):
        tg, _ = gate.issue_session(config(), "tg", str(OWNER), NOW)
        key, _ = gate.issue_session(config(), "key", "browser", NOW)
        for changed in (config(ACCESS_KEY="n" * 40), config(SESSION_SALT="2026-10")):
            self.assertIsNone(gate.verify_session(changed, tg, NOW))
            self.assertIsNone(gate.verify_session(changed, key, NOW))

    def test_clearing_the_key_ends_key_sessions(self):
        value, _ = gate.issue_session(config(), "key", "browser", NOW)
        # Same derived signing key would differ anyway; the point is the explicit gate.
        cfg = config(ACCESS_KEY="")
        forged, _ = gate.issue_session(cfg, "key", "browser", NOW)
        self.assertIsNone(gate.verify_session(cfg, forged, NOW))
        self.assertIsNone(gate.verify_session(cfg, value, NOW))

    def test_cookie_flavours(self):
        top = gate.cookie_headers("v", 60, embedded=False)[0]
        framed = gate.cookie_headers("v", 60, embedded=True)[0]
        for c in (top, framed):
            self.assertTrue(c.startswith("__Host-horus_monitor=v; Path=/;"))
            self.assertIn("Secure", c)
            self.assertIn("HttpOnly", c)
        self.assertIn("SameSite=Lax", top)
        self.assertNotIn("Partitioned", top)
        self.assertIn("SameSite=None; Partitioned", framed)


class HandoffTests(unittest.TestCase):

    def test_a_link_works_once(self):
        cfg, h = config(), gate.Handoffs()
        token = h.issue(cfg, OWNER, NOW)
        self.assertEqual(OWNER, h.redeem(cfg, token, NOW + 5))
        self.assertIsNone(h.redeem(cfg, token, NOW + 6))

    def test_a_link_dies_after_a_minute(self):
        cfg, h = config(), gate.Handoffs()
        self.assertIsNone(h.redeem(cfg, h.issue(cfg, OWNER, NOW), NOW + gate.HANDOFF_TTL_S))

    def test_a_link_and_a_session_are_not_interchangeable(self):
        cfg, h = config(), gate.Handoffs()
        session, _ = gate.issue_session(cfg, "tg", str(OWNER), NOW)
        self.assertIsNone(h.redeem(cfg, session, NOW))
        self.assertIsNone(gate.verify_session(cfg, h.issue(cfg, OWNER, NOW), NOW))

    def test_a_tampered_link_or_a_removed_user_is_refused(self):
        cfg, h = config(), gate.Handoffs()
        token = h.issue(cfg, OWNER, NOW)
        self.assertIsNone(h.redeem(cfg, token.replace(f"h1.{OWNER}.", "h1.222."), NOW))
        self.assertIsNone(h.redeem(config(TG_ALLOWED_USERS="333"), h.issue(cfg, OWNER, NOW), NOW))


class ConfigTests(unittest.TestCase):

    def test_defaults(self):
        cfg = config()
        self.assertEqual("https://monitor.example.com", cfg.origin)
        self.assertEqual(720 * 3600, cfg.key_session_s)
        self.assertEqual("Мониторинг", cfg.menu_text)
        self.assertTrue(cfg.tg_enabled and cfg.key_enabled)

    def test_several_users(self):
        self.assertEqual(frozenset({1, 2, 3}), config(TG_ALLOWED_USERS=" 1, 2;3 ").tg_users)

    def test_mistakes_fail_loudly(self):
        for env in ({"MONITOR_DOMAIN": ""},
                    {"MONITOR_DOMAIN": "https://monitor.example.com"},
                    {"TG_ALLOWED_USERS": "-1001234567890"},   # a group chat id, not a person
                    {"TG_ALLOWED_USERS": "owner"},
                    {"ACCESS_KEY": "short"},
                    {"PROBE_PASSWORD": "short"},
                    {"SESSION_HOURS_TG": "0"},
                    {"TELEGRAM_BOT_TOKEN": ""},
                    {"TG_ALLOWED_USERS": "", "ACCESS_KEY": ""}):
            with self.subTest(env=env), self.assertRaises(gate.ConfigError):
                config(**env)

    def test_key_only_needs_no_bot(self):
        cfg = config(TELEGRAM_BOT_TOKEN="", TG_ALLOWED_USERS="")
        self.assertFalse(cfg.tg_enabled)
        self.assertTrue(cfg.key_enabled)


class HelperTests(unittest.TestCase):

    def test_next_stays_on_this_site(self):
        for bad in (None, "", "https://evil.example", "//evil.example", "/\\evil.example",
                    "/vmui/\nSet-Cookie: x", "/auth/logout", "/auth", "vmui"):
            with self.subTest(bad=bad):
                self.assertEqual(gate.DEFAULT_NEXT, gate.safe_next(bad))
        self.assertEqual("/vmui/#/cardinality", gate.safe_next("/vmui/#/cardinality"))
        self.assertEqual("/vmalert/groups", gate.safe_next("/vmalert/groups"))

    def test_failures_brake_and_release(self):
        f = gate.Failures(limit=3, window_s=100)
        for i in range(3):
            self.assertEqual(0, f.retry_after("1.2.3.4", NOW + i))
            f.record("1.2.3.4", NOW + i)
        self.assertGreater(f.retry_after("1.2.3.4", NOW + 3), 0)
        self.assertEqual(0, f.retry_after("5.6.7.8", NOW + 3))
        self.assertEqual(0, f.retry_after("1.2.3.4", NOW + 101))


# ── Over HTTP, the way Caddy talks to it ─────────────────────────────────────

class HttpTests(unittest.TestCase):

    def setUp(self):
        self.now = [float(NOW)]
        self.cfg = config()
        self.failures = gate.Failures(limit=10, window_s=900)
        handler = gate.make_handler(self.cfg, self.failures, clock=lambda: self.now[0])
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def request(self, method, path, body=None, headers=None, ip="203.0.113.7"):
        conn = http.client.HTTPConnection("127.0.0.1", self.server.server_address[1], timeout=5)
        hdrs = {"X-Forwarded-For": ip, **(headers or {})}
        if isinstance(body, dict):
            body = urllib.parse.urlencode(body)
            hdrs.setdefault("Content-Type", "application/x-www-form-urlencoded")
        conn.request(method, path, body=body, headers=hdrs)
        resp = conn.getresponse()
        resp.text = resp.read().decode()
        conn.close()
        return resp

    def cookie_from(self, resp):
        value = resp.getheader("Set-Cookie").split(";")[0]
        self.assertTrue(value.startswith(gate.COOKIE + "="))
        return value

    def tg_login(self, user_id=OWNER, embedded="0", **headers):
        return self.request("POST", "/auth/telegram",
                            {"init_data": sign_init_data(launch(user_id, auth_date=NOW)), "embedded": embedded},
                            headers)

    # ── forward_auth ──

    def test_a_page_load_without_a_session_goes_to_the_login_page(self):
        r = self.request("GET", "/auth/check", headers={
            "Accept": "text/html,application/xhtml+xml", "X-Forwarded-Method": "GET",
            "X-Forwarded-Uri": "/vmui/?x=1"})
        self.assertEqual(302, r.status)
        self.assertEqual("/auth/?next=%2Fvmui%2F%3Fx%3D1", r.getheader("Location"))

    def test_an_api_call_without_a_session_is_a_plain_401(self):
        r = self.request("GET", "/auth/check", headers={
            "Accept": "application/json", "X-Forwarded-Uri": "/prometheus/api/v1/query?query=up"})
        self.assertEqual(401, r.status)
        self.assertEqual("unauthorized", json.loads(r.text)["error"])

    def test_a_session_cookie_passes(self):
        cookie = self.cookie_from(self.tg_login())
        self.assertEqual(204, self.request("GET", "/auth/check", headers={"Cookie": cookie}).status)

    def test_a_bearer_key_passes_and_a_wrong_one_is_braked(self):
        ok = self.request("GET", "/auth/check", headers={"Authorization": "Bearer " + KEY})
        self.assertEqual(204, ok.status)
        for _ in range(10):
            self.assertEqual(401, self.request("GET", "/auth/check", headers={"Authorization": "Bearer nope"}).status)
        braked = self.request("GET", "/auth/check", headers={"Authorization": "Bearer " + KEY})
        self.assertEqual(429, braked.status)
        self.assertTrue(int(braked.getheader("Retry-After")) > 0)

    # ── Telegram ──

    def test_the_owner_gets_a_lax_cookie(self):
        r = self.tg_login(Origin="https://monitor.example.com")
        self.assertEqual(200, r.status)
        self.assertIn("SameSite=Lax", r.getheader("Set-Cookie"))

    def test_inside_telegram_web_the_cookie_is_partitioned(self):
        r = self.tg_login(embedded="1")
        self.assertIn("SameSite=None; Partitioned", r.getheader("Set-Cookie"))

    def test_only_an_embedded_login_gets_a_handoff_link(self):
        self.assertNotIn("handoff", json.loads(self.tg_login().text))
        link = json.loads(self.tg_login(embedded="1").text)["handoff"]
        first = self.request("GET", link)
        self.assertEqual(303, first.status)
        self.assertEqual(gate.DEFAULT_NEXT, first.getheader("Location"))
        # The new tab is a top-level window: an ordinary Lax cookie, not a partitioned one.
        self.assertIn("SameSite=Lax", first.getheader("Set-Cookie"))
        again = self.request("GET", link)
        self.assertEqual("/auth/?error=handoff", again.getheader("Location"))
        self.assertIsNone(again.getheader("Set-Cookie"))

    def test_a_stranger_is_refused(self):
        r = self.tg_login(STRANGER)
        self.assertEqual(403, r.status)
        self.assertEqual("not_allowed", json.loads(r.text)["error"])
        self.assertIsNone(r.getheader("Set-Cookie"))

    def test_a_login_posted_from_another_site_is_refused(self):
        self.assertEqual(403, self.tg_login(Origin="https://evil.example").status)

    def test_an_early_refusal_still_consumes_the_body(self):
        # Caddy reuses its connection to the gate. A refused POST whose body was left
        # unread would turn into garbage at the head of the next request on it.
        conn = http.client.HTTPConnection("127.0.0.1", self.server.server_address[1], timeout=5)
        body = urllib.parse.urlencode({"init_data": sign_init_data(launch()), "embedded": "0"})
        conn.request("POST", "/auth/telegram", body=body, headers={
            "Content-Type": "application/x-www-form-urlencoded", "Origin": "https://evil.example"})
        first = conn.getresponse(); first.read()
        self.assertEqual(403, first.status)
        conn.request("GET", "/auth/whoami")
        second = conn.getresponse(); second.read()
        self.assertEqual(401, second.status)       # a clean answer, not a 400 for garbage
        conn.close()

    def test_an_oversized_body_is_refused_and_the_connection_closed(self):
        r = self.request("POST", "/auth/key", "x" * (gate.MAX_BODY + 1),
                         {"Content-Type": "application/x-www-form-urlencoded"})
        self.assertEqual(413, r.status)
        self.assertEqual("close", (r.getheader("Connection") or "").lower())

    # ── Access key ──

    def test_the_right_key_lands_on_next(self):
        r = self.request("POST", "/auth/key", {"key": KEY, "next": "/vmui/#/cardinality"})
        self.assertEqual(303, r.status)
        self.assertEqual("/vmui/#/cardinality", r.getheader("Location"))
        self.assertIn("SameSite=Lax", r.getheader("Set-Cookie"))
        who = self.request("GET", "/auth/whoami", headers={"Cookie": self.cookie_from(r)})
        self.assertEqual("key", json.loads(who.text)["kind"])

    def test_next_cannot_leave_the_site(self):
        r = self.request("POST", "/auth/key", {"key": KEY, "next": "//evil.example/"})
        self.assertEqual(gate.DEFAULT_NEXT, r.getheader("Location"))

    def test_a_wrong_key_goes_back_with_an_error_and_the_brake_engages(self):
        for _ in range(10):
            r = self.request("POST", "/auth/key", {"key": "wrong", "next": "/vmui/"})
            self.assertEqual(303, r.status)
            self.assertTrue(r.getheader("Location").endswith("&error=key"))
            self.assertIsNone(r.getheader("Set-Cookie"))
        r = self.request("POST", "/auth/key", {"key": KEY, "next": "/vmui/"})
        self.assertIn("&error=slow&retry=", r.getheader("Location"))
        self.assertIsNone(r.getheader("Set-Cookie"))
        # Another address is not affected.
        self.assertIsNotNone(self.request("POST", "/auth/key", {"key": KEY}, ip="198.51.100.1").getheader("Set-Cookie"))

    # ── Probe ──

    def basic(self, password):
        import base64
        return {"Authorization": "Basic " + base64.b64encode(f"horus:{password}".encode()).decode()}

    def test_the_probe_password_opens_only_the_probe_route(self):
        self.assertEqual(204, self.request("GET", "/auth/check-probe", headers=self.basic(PROBE)).status)
        self.assertEqual(401, self.request("GET", "/auth/check-probe", headers=self.basic("wrong-password-x")).status)
        # A browser session is not a probe credential.
        cookie = self.cookie_from(self.tg_login())
        self.assertEqual(401, self.request("GET", "/auth/check-probe", headers={"Cookie": cookie}).status)
        # And the probe password is not a browser key.
        self.assertEqual(401, self.request("GET", "/auth/check", headers={"Authorization": "Bearer " + PROBE}).status)

    # ── Pages ──

    def test_the_login_page_says_which_ways_in_are_on(self):
        r = self.request("GET", "/auth/")
        self.assertEqual(200, r.status)
        self.assertIn('data-tg="1" data-key="1"', r.text)
        self.assertIn("frame-ancestors 'self' https://web.telegram.org", r.getheader("Content-Security-Policy"))
        self.assertEqual("no-store", r.getheader("Cache-Control"))

    def test_logout_clears_both_cookie_flavours(self):
        r = self.request("GET", "/auth/logout")
        self.assertEqual(303, r.status)
        cookies = r.headers.get_all("Set-Cookie")
        self.assertEqual(2, len(cookies))
        self.assertTrue(all("Max-Age=0" in c for c in cookies))


class MenuButtonTests(unittest.TestCase):
    """setChatMenuButton against a stand-in for api.telegram.org."""

    def serve(self, status):
        calls = []

        class Api(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"])).decode()
                calls.append((self.path, dict(urllib.parse.parse_qsl(body))))
                payload = json.dumps({"ok": status == 200, "description": "Bad Request: chat not found"}).encode()
                self.send_response(status)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

        server = ThreadingHTTPServer(("127.0.0.1", 0), Api)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_address[1]}", calls

    def test_each_allowed_user_gets_the_button(self):
        api, calls = self.serve(200)
        result = gate.set_menu_buttons(config(TG_ALLOWED_USERS="111,222"), api=api, pause_s=0)
        self.assertEqual({111: "ok", 222: "ok"}, result)
        path, form = calls[0]
        self.assertEqual(f"/bot{TOKEN}/setChatMenuButton", path)
        self.assertEqual("111", form["chat_id"])
        button = json.loads(form["menu_button"])
        self.assertEqual({"type": "web_app", "text": "Мониторинг",
                          "web_app": {"url": "https://monitor.example.com/auth/"}}, button)

    def test_a_refusal_is_not_retried(self):
        api, calls = self.serve(400)
        result = gate.set_menu_buttons(config(), api=api, pause_s=0)
        self.assertEqual(1, len(calls))
        self.assertTrue(result[OWNER].startswith("refused"))

    def test_an_empty_label_leaves_the_bot_alone(self):
        api, calls = self.serve(200)
        self.assertEqual({}, gate.set_menu_buttons(config(TG_MENU_BUTTON=""), api=api, pause_s=0))
        self.assertEqual([], calls)


if __name__ == "__main__":
    unittest.main()
