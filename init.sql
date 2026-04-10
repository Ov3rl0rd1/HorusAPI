-- ============================================================
--  VPN Auth API – PostgreSQL schema
-- ============================================================

-- Users who can authenticate against the API
CREATE TABLE IF NOT EXISTS users (
    id            SERIAL PRIMARY KEY,
    username      VARCHAR(64)  NOT NULL UNIQUE,
    password_hash VARCHAR(256) NOT NULL,          -- BCrypt hash
    api_key
    email         VARCHAR(128),
    is_active     BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    expires_at    TIMESTAMPTZ                  -- NULL = never expires
);

-- VPN server pool
CREATE TABLE IF NOT EXISTS vpn_servers (
    id            SERIAL PRIMARY KEY,
    name          VARCHAR(128) NOT NULL,
    country       VARCHAR(64)  NOT NULL,
    city          VARCHAR(64)  NOT NULL,
    host          VARCHAR(256) NOT NULL,           -- hostname or IP
    protocol      VARCHAR(32)  NOT NULL DEFAULT 'Hysteria2',
    current_load  INTEGER      NOT NULL DEFAULT 0, -- connected clients count
    max_clients   INTEGER      NOT NULL DEFAULT 5,
    is_active     BOOLEAN      NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS idx_users_username     ON users(username);
CREATE INDEX IF NOT EXISTS idx_servers_is_active  ON vpn_servers(is_active);

-- ============================================================
--  Seed data – CHANGE PASSWORDS BEFORE PRODUCTION USE
-- ============================================================

-- Demo user (password: "demo1234")
-- Hash generated with BCrypt work factor 12
INSERT INTO users (username, password_hash, email, is_active)
VALUES (
    'demo',
    '$2a$12$92P8ER8nJDpZ5JTBkn8yLeAKE3YwHv0kPb.yINkB08.RZvKe9w7Xi',
    'demo@example.com',
    TRUE
) ON CONFLICT (username) DO NOTHING;

INSERT INTO vpn_servers (name, country, city, host, protocol, current_load, max_clients)
VALUES
    ('FN-HEL-01', 'Finland', 'Helsinki', 'fn1.horuspingbuster.ru', 'Hysteria2', 0, 5),
ON CONFLICT DO NOTHING;