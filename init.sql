--  VPN Auth API – PostgreSQL schema

CREATE TABLE IF NOT EXISTS users (
    id            SERIAL PRIMARY KEY,
    username      VARCHAR(64)  NOT NULL UNIQUE,
    password_hash VARCHAR(256) NOT NULL,
    sessions      VARCHAR(64)[],
    email         VARCHAR(128),
    is_active     BOOLEAN      NOT NULL DEFAULT TRUE,
    is_admin      BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    expires_at    TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS vpn_servers (
    id             SERIAL PRIMARY KEY,
    name           VARCHAR(128) NOT NULL,
    country        VARCHAR(64)  NOT NULL,
    city           VARCHAR(64)  NOT NULL,
    host           VARCHAR(256) NOT NULL,
    current_load   INTEGER      NOT NULL DEFAULT 0,
    max_clients    INTEGER      NOT NULL DEFAULT 5,
    is_active      BOOLEAN      NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS idx_users_username       ON users(username);
CREATE INDEX IF NOT EXISTS idx_servers_is_active    ON vpn_servers(is_active);

-- ============================================================
--  First-run instructions
--  After starting the stack, create your admin account via:
--
--  POST https://<host>/auth/register
--  {"username":"admin","password":"<strong-password>","email":"<your-email>"}
--
--  Then grant admin rights directly in the database:
--  UPDATE users SET is_admin = TRUE WHERE username = 'admin';
--
--  Optional: seed a VPN server (example below, remove before production)
-- ============================================================

-- INSERT INTO vpn_servers (name, country, city, host, current_load, max_clients)
-- VALUES ('MY-SRV-01', 'Finland', 'Helsinki', 'srv1.example.com', 0, 50)
-- ON CONFLICT DO NOTHING;
