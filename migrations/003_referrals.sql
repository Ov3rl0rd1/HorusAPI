-- ============================================================================
--  003 — referral partners
--
--  init.sql only runs on a fresh database (docker-entrypoint-initdb.d), so an
--  existing deployment needs this applied by hand:
--
--    docker compose exec -T postgres psql -U <user> -d <db> < migrations/003_referrals.sql
--
--  Idempotent — safe to re-run. The same statements are in init.sql.
--
--  WHY: partners (bloggers, friends of the service) get a code from the admin.
--  New customers who come with it get a discount, and the partner earns a share
--  of what they pay. See Services/Billing/ReferralService.cs.
-- ============================================================================

-- ============================================================================
--  Referral partners  (see Services/Billing/ReferralService.cs)
--
--  A partner is a user the admin has given a code. A NEW customer who signs up
--  with it (?ref=CODE, or the code typed into the promo field at checkout) is
--  bound to that partner for good — first code wins — gets discount_percent off
--  every purchase while the partner stays active, and the partner earns
--  reward_percent of every ruble that customer pays. Payouts are made by hand
--  and recorded here; balance = accrued rewards − payouts.
--  Codes share one namespace with promo_codes (both arrive in the same field).
-- ============================================================================
CREATE TABLE IF NOT EXISTS referral_partners (
    user_id          INT          PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    code             VARCHAR(64)  NOT NULL,
    discount_percent SMALLINT     NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 90),
    reward_percent   SMALLINT     NOT NULL DEFAULT 0 CHECK (reward_percent BETWEEN 0 AND 100),
    is_active        BOOLEAN      NOT NULL DEFAULT TRUE,
    note             VARCHAR(256),
    created_by       INT          REFERENCES users(id) ON DELETE SET NULL,
    created_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_referral_partners_code ON referral_partners (lower(code));

-- Who brought this customer. SET NULL, not CASCADE: a partner account going away
-- must not take its customers with it.
ALTER TABLE users ADD COLUMN IF NOT EXISTS referred_by INT REFERENCES users(id) ON DELETE SET NULL;
ALTER TABLE users ADD COLUMN IF NOT EXISTS referred_at TIMESTAMPTZ;
CREATE INDEX IF NOT EXISTS idx_users_referred_by ON users (referred_by) WHERE referred_by IS NOT NULL;

-- The partner whose discount the checkout considered (NULL = none).
ALTER TABLE payments ADD COLUMN IF NOT EXISTS referrer_id INT REFERENCES users(id) ON DELETE SET NULL;

-- One row per paid period / purchase of a referred customer. `source` is the
-- idempotency key ("payment:<id>" for a one-time buy, "subscription:<id>:<period
-- end date>" for a recurring period), so a replayed webhook never pays twice.
-- A refund or chargeback marks the purchase's latest reward 'reversed'.
CREATE TABLE IF NOT EXISTS referral_rewards (
    id               SERIAL PRIMARY KEY,
    partner_id       INT          NOT NULL REFERENCES referral_partners(user_id) ON DELETE CASCADE,
    referred_user_id INT          REFERENCES users(id) ON DELETE SET NULL,
    subscription_id  INT          REFERENCES subscriptions(id) ON DELETE SET NULL,
    payment_id       INT          REFERENCES payments(id) ON DELETE SET NULL,
    source           VARCHAR(160) NOT NULL UNIQUE,
    paid_amount      INT          NOT NULL,      -- what the customer paid, whole rubles
    percent          SMALLINT     NOT NULL,      -- the partner's share at the time
    amount           INT          NOT NULL,      -- the partner's reward, whole rubles (rounded down)
    status           VARCHAR(16)  NOT NULL DEFAULT 'accrued',   -- 'accrued' | 'reversed'
    created_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    reversed_at      TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_referral_rewards_partner ON referral_rewards (partner_id);
CREATE INDEX IF NOT EXISTS idx_referral_rewards_subscription ON referral_rewards (subscription_id);

-- Money actually handed to a partner, recorded by an admin.
CREATE TABLE IF NOT EXISTS referral_payouts (
    id          SERIAL PRIMARY KEY,
    partner_id  INT          NOT NULL REFERENCES referral_partners(user_id) ON DELETE CASCADE,
    amount      INT          NOT NULL CHECK (amount > 0),
    note        VARCHAR(256),
    created_by  INT          REFERENCES users(id) ON DELETE SET NULL,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_referral_payouts_partner ON referral_payouts (partner_id);

