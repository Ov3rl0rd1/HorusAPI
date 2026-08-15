--  VPN Auth API – PostgreSQL schema
--  Brought in line with the services + the xray-core node model.

CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- gen_random_uuid()

-- ============================================================================
--  users
-- ============================================================================
CREATE TABLE IF NOT EXISTS users (
    id                  SERIAL PRIMARY KEY,
    username            VARCHAR(64)  NOT NULL UNIQUE,
    password_hash       VARCHAR(256) NOT NULL,
    sessions            VARCHAR(64)[],
    email               VARCHAR(128),
    email_verified      BOOLEAN      NOT NULL DEFAULT FALSE,   -- set by POST /auth/verify (6-digit code)
    vpn_uuid            UUID         NOT NULL DEFAULT gen_random_uuid(),      -- per-user VLESS identity (assigned on first node pull)
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    is_admin            BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ,                 -- NULL = never expires
    current_server_id      INT,
    last_connect_at        TIMESTAMPTZ,
    last_disconnect_at     TIMESTAMPTZ,
    last_disconnect_reason VARCHAR(16)
);

-- ============================================================================
--  vpn_servers  (one row per node)
-- ============================================================================
CREATE TABLE IF NOT EXISTS vpn_servers (
    id             SERIAL PRIMARY KEY,
    name           VARCHAR(128) NOT NULL,
    country        VARCHAR(64)  NOT NULL,
    city           VARCHAR(64)  NOT NULL,
    host           VARCHAR(256) NOT NULL,
    current_load   INTEGER      NOT NULL DEFAULT 0,   -- reused as the online-user count
    max_clients    INTEGER      NOT NULL DEFAULT 5,
    is_active      BOOLEAN      NOT NULL DEFAULT TRUE,

    -- Shared secret with this node's agent (sent/received as X-API-PASSWORD).
    auth_password  VARCHAR(128) NOT NULL DEFAULT '',

    obfs_password  VARCHAR(128) NOT NULL DEFAULT '',
    hop            VARCHAR(64)  NOT NULL DEFAULT '',
    masquerade_url VARCHAR(256),

    -- Public parameters reported by the node agent (POST /node/register), used to
    -- hand clients a working config.
    reality_public_key  VARCHAR(128) NOT NULL DEFAULT '',
    reality_short_ids   TEXT[]       NOT NULL DEFAULT '{}',
    reality_server_name VARCHAR(256) NOT NULL DEFAULT '',
    reality_dest        VARCHAR(256) NOT NULL DEFAULT '',
    vless_port          INTEGER      NOT NULL DEFAULT 443,
    hysteria_port       INTEGER      NOT NULL DEFAULT 8443,
    olcrtc_provider     VARCHAR(32)  NOT NULL DEFAULT '',
    olcrtc_transport    VARCHAR(32)  NOT NULL DEFAULT '',
    olcrtc_room_id      VARCHAR(256) NOT NULL DEFAULT '',
    olcrtc_room_key     VARCHAR(128) NOT NULL DEFAULT '',   -- shared with clients out-of-band
    agent_version       VARCHAR(32)  NOT NULL DEFAULT '',
    last_registered_at  TIMESTAMPTZ
);

-- ============================================================================
--  email_verifications  (at most one pending 6-digit code per user)
--  The code itself is never stored — only sha256("{user_id}:{code}") as hex.
-- ============================================================================
CREATE TABLE IF NOT EXISTS email_verifications (
    user_id    INT         PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    code_hash  VARCHAR(64) NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    attempts   SMALLINT    NOT NULL DEFAULT 0,   -- wrong guesses; the row dies at 5
    sent_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================================
--  password_resets  (one row per emailed reset link)
--  Only sha256(token) is stored, so a DB leak cannot be replayed as a link.
-- ============================================================================
CREATE TABLE IF NOT EXISTS password_resets (
    token_hash VARCHAR(64) PRIMARY KEY,
    user_id    INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    expires_at TIMESTAMPTZ NOT NULL,
    used_at    TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_users_username    ON users(username);
CREATE INDEX IF NOT EXISTS idx_users_sessions    ON users USING GIN (sessions);
CREATE INDEX IF NOT EXISTS idx_servers_is_active ON vpn_servers(is_active);
CREATE INDEX IF NOT EXISTS idx_servers_auth_pw   ON vpn_servers(auth_password);
CREATE INDEX IF NOT EXISTS idx_users_current_server ON users(current_server_id);
CREATE INDEX IF NOT EXISTS idx_password_resets_user  ON password_resets(user_id);

-- One account per address, and the index behind case-insensitive e-mail lookups:
-- /auth/verify, /auth/reset-request, and single-field /auth/login (username OR email)
-- all resolve users via lower(email), which this index serves. Partial so the legacy
-- rows with a NULL/empty email stay valid.
CREATE UNIQUE INDEX IF NOT EXISTS users_email_lower_key
    ON users (lower(email)) WHERE email IS NOT NULL AND email <> '';

-- ============================================================================
--  Idempotent upgrades for pre-existing databases (safe to re-run)
-- ============================================================================
ALTER TABLE users ADD COLUMN IF NOT EXISTS vpn_uuid UUID;

-- Accounts that already existed predate email confirmation, so they are
-- grandfathered in as verified (DEFAULT TRUE backfills them); every account
-- created from now on starts unverified.
ALTER TABLE users ADD COLUMN IF NOT EXISTS email_verified BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE users ALTER COLUMN email_verified SET DEFAULT FALSE;

ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS auth_password       VARCHAR(128) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS obfs_password       VARCHAR(128) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS hop                 VARCHAR(64)  NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS masquerade_url      VARCHAR(256);
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS reality_public_key  VARCHAR(128) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS reality_short_ids   TEXT[]       NOT NULL DEFAULT '{}';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS reality_server_name VARCHAR(256) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS reality_dest        VARCHAR(256) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS vless_port          INTEGER      NOT NULL DEFAULT 443;
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS hysteria_port       INTEGER      NOT NULL DEFAULT 8443;
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS olcrtc_provider     VARCHAR(32)  NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS olcrtc_transport    VARCHAR(32)  NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS olcrtc_room_id      VARCHAR(256) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS olcrtc_room_key     VARCHAR(128) NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS agent_version       VARCHAR(32)  NOT NULL DEFAULT '';
ALTER TABLE vpn_servers ADD COLUMN IF NOT EXISTS last_registered_at  TIMESTAMPTZ;

ALTER TABLE users ADD COLUMN IF NOT EXISTS current_server_id      INT;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_connect_at        TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_disconnect_at     TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_disconnect_reason VARCHAR(16);

-- ============================================================================
--  First-run
--  1) POST /auth/register  {"username":"admin","password":"…","email":"…"}
--  2) UPDATE users SET is_admin = TRUE WHERE username = 'admin';
--  3) Add a node with POST /admin/servers, setting auth_password to that node's
--     NODE_API_PASSWORD. The node fills in the reality_*/olcrtc_* columns itself
--     when its agent calls POST /node/register.
-- ============================================================================
