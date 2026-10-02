-- ============================================================================
--  004 — per-user monthly traffic, kept centrally
--
--  init.sql only runs on a fresh database (docker-entrypoint-initdb.d), so an
--  existing deployment needs this applied by hand, BEFORE the new API starts:
--
--    docker compose exec -T postgres psql -U <user> -d <db> < migrations/004_traffic_usage.sql
--
--  Idempotent — safe to re-run. The same statement is in init.sql.
--
--  WHY: monthly allowances (overall and olcRTC) were counted only inside xray on
--  each node, so moving to another server — by choice, or by an evacuation —
--  started a user's month from zero.
-- ============================================================================

-- ============================================================================
--  traffic_usage  (see Services/TrafficService.cs)
--
--  Each user's traffic per calendar month (UTC), counted against their monthly
--  allowances: total_bytes is everything, olcrtc_bytes the olcRTC part of it.
--  Kept HERE, per user, and not on the nodes, so a month's allowance follows the
--  user from server to server: nodes report it as it grows (/node/events, and the
--  answer to DELETE /users/{uuid}), and POST /users hands it to the next node,
--  which restores it into xray before the user's first byte there.
--
--  Reports are absolute month-to-date figures and are merged with GREATEST, so a
--  retried, repeated or late report can never add anything twice.
-- ============================================================================
CREATE TABLE IF NOT EXISTS traffic_usage (
    user_id      INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    month        DATE        NOT NULL,          -- first day of the month, UTC
    total_bytes  BIGINT      NOT NULL DEFAULT 0 CHECK (total_bytes >= 0),
    olcrtc_bytes BIGINT      NOT NULL DEFAULT 0 CHECK (olcrtc_bytes >= 0),
    server_id    INT         REFERENCES vpn_servers(id) ON DELETE SET NULL,   -- who reported last
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, month)
);

