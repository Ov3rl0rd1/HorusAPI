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
    current_load   INTEGER      NOT NULL DEFAULT 0,   -- live online count, reported by node telemetry (/node/events)
    reserved_count INTEGER      NOT NULL DEFAULT 0,   -- slots held by bound users (current_server_id) + pending holds
    -- Two capacity numbers:
    --   max_reservations = the HARD physical cap. Overselling is blocked on THIS
    --                      (reserved_count = max_reservations → full; buy/select refused).
    --   max_clients      = a SOFT "recommended" threshold, ~1.5× smaller. Purely advisory:
    --                      the client shows a node as heavily loaded once reserved_count
    --                      reaches it, but reservations keep succeeding up to max_reservations.
    max_clients      INTEGER    NOT NULL DEFAULT 5,
    max_reservations INTEGER    NOT NULL DEFAULT 8,
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
    last_registered_at  TIMESTAMPTZ,

    -- ── xray profiles ───────────────────────────────────────────────────────
    -- What the node is ACTUALLY running, reported by POST /node/register.
    profile        VARCHAR(64) NOT NULL DEFAULT '',
    profile_hash   VARCHAR(80) NOT NULL DEFAULT '',   -- sha256 of the profile source
    config_hash    VARCHAR(80) NOT NULL DEFAULT '',   -- sha256 of the rendered config.json

    -- What we WANT it to run. NULL = follow fleet_settings.default_profile.
    -- Setting this is how a protocol is switched without touching the node.
    desired_profile VARCHAR(64),

    -- The node's client-facing offers: whole xray outbounds carrying a ${uuid}
    -- placeholder. Stored opaquely and replayed with the user substituted, which is
    -- what lets /connect serve a protocol this API knows nothing about.
    offers         JSONB NOT NULL DEFAULT '[]'::jsonb,

    -- Set when the node could not render its profile (it keeps serving the previous
    -- config). Surfaced in the admin view so a typo is visible rather than silent.
    render_error   TEXT,
    warnings       TEXT[] NOT NULL DEFAULT '{}'
);

-- ============================================================================
--  fleet_settings  (exactly one row)
--  The fleet-wide default profile. A node uses COALESCE(desired_profile,
--  default_profile), so one UPDATE here moves every node that has no override.
-- ============================================================================
CREATE TABLE IF NOT EXISTS fleet_settings (
    id              SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    default_profile VARCHAR(64) NOT NULL DEFAULT '',   -- '' = let each node decide
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO fleet_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

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

-- Server selection: least-loaded-with-capacity, per country. Partial on active nodes.
CREATE INDEX IF NOT EXISTS idx_servers_available
    ON vpn_servers(country, reserved_count) WHERE is_active;

-- vpn_uuid is the per-user identity used on nodes and in every link; node telemetry
-- (/node/events) maps uuid → user through this unique index.
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_vpn_uuid ON users(vpn_uuid);

-- One account per address, and the index behind case-insensitive e-mail lookups:
-- /auth/verify, /auth/reset-request, and single-field /auth/login (username OR email)
-- all resolve users via lower(email), which this index serves. Partial so the legacy
-- rows with a NULL/empty email stay valid.
CREATE UNIQUE INDEX IF NOT EXISTS users_email_lower_key
    ON users (lower(email)) WHERE email IS NOT NULL AND email <> '';

-- ============================================================================
--  Billing: plans, subscriptions (source of access truth), payments, promos
--  ---------------------------------------------------------------------------
--  Access model: a user has access iff they are an admin OR they hold a live
--  subscription. `subscriptions` is the source of truth; `users.expires_at` is a
--  denormalised cache recomputed from it (the hot auth path reads only the cache).
--  MONEY: every `amount` column is WHOLE RUBLES (integer), matching Platega's
--  `amount` unit — there is no minor-unit (kopeck) conversion anywhere.
-- ============================================================================

-- Tariff catalogue. Seeded manually by the operator (ships empty on purpose).
--   kind='recurring' → Platega paymentMethod 6 subscription (auto-renews).
--   kind='one_time'  → single payment granting interval_unit*interval_count of access.
--   is_public=false  → "для своих": hidden, only visible/buyable with a plan_grant.
CREATE TABLE IF NOT EXISTS plans (
    id             SERIAL PRIMARY KEY,
    code           VARCHAR(64)  NOT NULL UNIQUE,
    title          VARCHAR(128) NOT NULL,
    tier           VARCHAR(16)  NOT NULL DEFAULT 'standard',   -- 'standard' | 'insider'
    kind           VARCHAR(16)  NOT NULL,                       -- 'recurring' | 'one_time'
    interval_unit  VARCHAR(8)   NOT NULL DEFAULT 'month',       -- 'day'|'week'|'month'|'year'
    interval_count INT          NOT NULL DEFAULT 1,
    amount         INT          NOT NULL,                       -- whole rubles per charge/purchase
    currency       VARCHAR(3)   NOT NULL DEFAULT 'RUB',
    is_public      BOOLEAN      NOT NULL DEFAULT TRUE,
    is_active      BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Access to a non-public plan for a specific user (the "для своих" grant).
CREATE TABLE IF NOT EXISTS plan_grants (
    id          SERIAL PRIMARY KEY,
    user_id     INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    plan_id     INT         NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    granted_by  INT         REFERENCES users(id) ON DELETE SET NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at  TIMESTAMPTZ,                                    -- NULL = never expires
    UNIQUE (user_id, plan_id)
);

-- The source of access truth. One row per subscription/purchase/comp grant.
--   status: 'pending'  checkout created, not yet paid (no access)
--           'active'   paying and current (access)
--           'past_due' a recurring charge failed; access lives until current_period_end
--           'canceled' auto-renew off (or user/admin canceled); access until current_period_end
--           'failed'   never activated (bind attempt failed at provider) — no access
--           'comp'     free service grant (access until current_period_end, usually far future)
CREATE TABLE IF NOT EXISTS subscriptions (
    id                   SERIAL PRIMARY KEY,
    user_id              INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    plan_id              INT         REFERENCES plans(id) ON DELETE SET NULL,  -- NULL for manual comp
    provider             VARCHAR(32) NOT NULL DEFAULT 'manual',               -- 'platega' | 'manual'
    provider_ref         VARCHAR(128),                                        -- Platega subscriptionId (recurring)
    kind                 VARCHAR(16) NOT NULL,                                -- 'recurring'|'one_time'|'comp'
    status               VARCHAR(16) NOT NULL,
    current_period_end   TIMESTAMPTZ,                                         -- access end; NULL until activated
    cancel_at_period_end BOOLEAN     NOT NULL DEFAULT FALSE,
    server_id            INT         REFERENCES vpn_servers(id) ON DELETE SET NULL,  -- reserved slot
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_subscriptions_user ON subscriptions(user_id);
-- One local subscription per provider subscription id (dedup + webhook correlation).
CREATE UNIQUE INDEX IF NOT EXISTS idx_subscriptions_provider_ref
    ON subscriptions(provider_ref) WHERE provider_ref IS NOT NULL;
-- Reconciliation sweeps look up "stuck" subscriptions by status.
CREATE INDEX IF NOT EXISTS idx_subscriptions_status ON subscriptions(status);

-- Promo codes (percent-off; schema left extensible via `kind`).
CREATE TABLE IF NOT EXISTS promo_codes (
    id              SERIAL PRIMARY KEY,
    code            VARCHAR(64) NOT NULL,
    kind            VARCHAR(16) NOT NULL DEFAULT 'percent',     -- only 'percent' today
    percent_off     SMALLINT    NOT NULL,                       -- 1..100
    max_redemptions INT,                                        -- NULL = unlimited (total)
    redeemed_count  INT         NOT NULL DEFAULT 0,
    per_user_limit  INT         DEFAULT 1,                      -- NULL = unlimited per user
    plan_id         INT         REFERENCES plans(id) ON DELETE CASCADE,  -- NULL = any plan
    starts_at       TIMESTAMPTZ,
    ends_at         TIMESTAMPTZ,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_promo_codes_code ON promo_codes(lower(code));

-- Pending slot reservation held during checkout, before payment confirms. The slot
-- is charged to vpn_servers.reserved_count the moment the hold is taken (so capacity
-- accounting stays entirely on reserved_count — candidate/select queries are unchanged);
-- a background sweeper releases holds whose expires_at has passed. At most one live
-- hold per user.
CREATE TABLE IF NOT EXISTS slot_holds (
    id          SERIAL PRIMARY KEY,
    user_id     INT         NOT NULL UNIQUE REFERENCES users(id) ON DELETE CASCADE,
    server_id   INT         NOT NULL REFERENCES vpn_servers(id) ON DELETE CASCADE,
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_slot_holds_expires ON slot_holds(expires_at);

-- A checkout intent and its outcome.
--   status: 'created'|'pending'|'confirmed'|'canceled'|'failed'|'refunded'|'chargebacked'
CREATE TABLE IF NOT EXISTS payments (
    id              SERIAL PRIMARY KEY,
    user_id         INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    plan_id         INT         REFERENCES plans(id) ON DELETE SET NULL,
    subscription_id INT         REFERENCES subscriptions(id) ON DELETE SET NULL,
    provider        VARCHAR(32) NOT NULL,
    provider_ref    VARCHAR(128),                              -- subscriptionId (recurring) / transactionId (one_time)
    kind            VARCHAR(16) NOT NULL,                      -- 'recurring'|'one_time'
    amount          INT         NOT NULL,                      -- whole rubles actually charged (after discount)
    currency        VARCHAR(3)  NOT NULL DEFAULT 'RUB',
    promo_code_id   INT         REFERENCES promo_codes(id) ON DELETE SET NULL,
    discount        INT         NOT NULL DEFAULT 0,            -- whole rubles taken off
    status          VARCHAR(16) NOT NULL,
    hold_id         INT         REFERENCES slot_holds(id) ON DELETE SET NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_payments_user ON payments(user_id);
CREATE INDEX IF NOT EXISTS idx_payments_provider_ref ON payments(provider_ref);

-- One row per recurring charge (Platega Id is unique per charge → idempotency key).
CREATE TABLE IF NOT EXISTS subscription_charges (
    id              SERIAL PRIMARY KEY,
    subscription_id INT         NOT NULL REFERENCES subscriptions(id) ON DELETE CASCADE,
    provider_txn_id VARCHAR(128) NOT NULL UNIQUE,
    amount          INT         NOT NULL,
    status          VARCHAR(16) NOT NULL,                      -- 'confirmed'|'canceled'|'chargebacked'
    charged_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    next_charge_at  TIMESTAMPTZ
);

-- Admin-initiated refunds (support-only; see AdminEndpoints).
CREATE TABLE IF NOT EXISTS refunds (
    id              SERIAL PRIMARY KEY,
    payment_id      INT         NOT NULL REFERENCES payments(id) ON DELETE CASCADE,
    admin_id        INT         REFERENCES users(id) ON DELETE SET NULL,
    amount          INT         NOT NULL,
    status          VARCHAR(16) NOT NULL,                      -- 'requested'|'accepted'|'manual_required'|'failed'
    provider_result TEXT,
    reason          VARCHAR(256),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Raw webhook log + idempotency guard. provider_event_id is a synthesised dedup key
-- ("{kind}:{primaryId}:{status}") because Platega reuses Id across a subscription's
-- lifecycle events. Re-processing is also safe by construction; this is the fast path + audit.
CREATE TABLE IF NOT EXISTS webhook_events (
    id                SERIAL PRIMARY KEY,
    provider          VARCHAR(32)  NOT NULL,
    provider_event_id VARCHAR(160) NOT NULL,
    kind              VARCHAR(48),
    received_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    processed_at      TIMESTAMPTZ,
    raw               JSONB,
    error             TEXT,
    UNIQUE (provider, provider_event_id)
);

CREATE TABLE IF NOT EXISTS promo_redemptions (
    id            SERIAL PRIMARY KEY,
    promo_code_id INT         NOT NULL REFERENCES promo_codes(id) ON DELETE CASCADE,
    user_id       INT         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    payment_id    INT         REFERENCES payments(id) ON DELETE SET NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_promo_redemptions_code_user ON promo_redemptions(promo_code_id, user_id);

-- ============================================================================
--  First-run
--  1) POST /auth/register  {"username":"admin","password":"…","email":"…"}
--  2) UPDATE users SET is_admin = TRUE WHERE username = 'admin';
--  3) Add a node with POST /admin/servers, setting auth_password to that node's
--     NODE_API_PASSWORD. The node fills in the reality_*/olcrtc_* columns itself
--     when its agent calls POST /node/register.
--  4) Seed at least one tariff (the catalogue ships empty), e.g.:
--       INSERT INTO plans (code, title, kind, interval_unit, interval_count, amount)
--       VALUES ('monthly', 'Месяц', 'recurring', 'month', 1, 199);
--     A "для своих" tariff is the same row with is_public = FALSE, made buyable per
--     user via POST /admin/users/{username}/grant.
-- ============================================================================
