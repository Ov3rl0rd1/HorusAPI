-- ============================================================================
--  002 — unverified-account flow
--
--  init.sql only runs on a fresh database (docker-entrypoint-initdb.d), so an
--  existing deployment needs this applied by hand:
--
--    docker compose exec -T postgres psql -U <user> -d <db> < migrations/002_unverified_accounts.sql
--
--  Idempotent — safe to re-run.
--
--  WHY: registration could only go forwards. A user who mistyped their address
--  had no way to fix it, no way to get back into the half-made account after
--  closing the tab, and the account sat in the table forever holding its
--  username and its e-mail against a unique index.
-- ============================================================================

-- ── pending_logins ──────────────────────────────────────────────────────────
--  A short-lived ticket that says "this caller proved they own an UNVERIFIED
--  account" and nothing else. It is not a session: SessionAuthHandler looks in
--  users.sessions[], never here, so a ticket cannot reach /servers, /billing or
--  anything else a real session opens.
--
--  Deliberately its own table rather than a column on email_verifications. The
--  ticket must outlive any single code — a user asks for three codes and keeps
--  one ticket — and email_verifications rows are replaced wholesale on every
--  send. Mirrors password_resets, which solves the same shape of problem.
--
--  Only sha256(token) is stored, so a database leak cannot be replayed.
CREATE TABLE IF NOT EXISTS pending_logins (
    token_hash VARCHAR(64) PRIMARY KEY,
    user_id    INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_pending_logins_user ON pending_logins (user_id);

-- ── Sweeping unverified accounts ────────────────────────────────────────────
--  Partial index so the sweeper's "old and unverified" scan stays off the main
--  table. Verified accounts are the overwhelming majority and never match.
CREATE INDEX IF NOT EXISTS idx_users_unverified
    ON users (created_at)
    WHERE email_verified = FALSE;
