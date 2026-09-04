-- ============================================================================
--  001 — xray profiles
--
--  init.sql only runs on a fresh database (docker-entrypoint-initdb.d), so an
--  existing deployment needs this applied by hand:
--
--    docker compose exec -T postgres psql -U <user> -d <db> < migrations/001_xray_profiles.sql
--
--  Idempotent — safe to re-run.
--
--  WHY: a node used to report a fixed list of protocol fields (reality_*,
--  olcrtc_*, ports) into fixed columns, and the API built vless:// / hysteria2://
--  links from them. That made every protocol change a schema + code change in two
--  repositories. Nodes now report `offers`: whole client-side xray outbounds with
--  a ${uuid} placeholder. The API stores them opaquely and substitutes the user,
--  so it can serve a protocol it knows nothing about.
--
--  The old columns are deliberately left in place. They stop being written once
--  the fleet is upgraded, and can be dropped later — see the bottom of this file.
-- ============================================================================

BEGIN;

ALTER TABLE vpn_servers
    -- What the node is ACTUALLY running (POST /node/register).
    ADD COLUMN IF NOT EXISTS profile         VARCHAR(64) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS profile_hash    VARCHAR(80) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS config_hash     VARCHAR(80) NOT NULL DEFAULT '',

    -- What we WANT it to run. NULL = follow fleet_settings.default_profile.
    -- Setting this is how a node's protocol is switched without touching the node.
    ADD COLUMN IF NOT EXISTS desired_profile VARCHAR(64),

    -- Client-facing offers, stored verbatim and replayed with ${uuid} substituted.
    ADD COLUMN IF NOT EXISTS offers          JSONB NOT NULL DEFAULT '[]'::jsonb,

    -- Set when the node could not render its profile (it keeps serving the previous
    -- config). Surfaced in the admin view so a typo is visible rather than silent.
    ADD COLUMN IF NOT EXISTS render_error    TEXT,
    ADD COLUMN IF NOT EXISTS warnings        TEXT[] NOT NULL DEFAULT '{}';

-- The fleet-wide default. A node resolves COALESCE(desired_profile,
-- default_profile), so one UPDATE here moves every node without an override.
CREATE TABLE IF NOT EXISTS fleet_settings (
    id              SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    default_profile VARCHAR(64) NOT NULL DEFAULT '',   -- '' = let each node decide
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO fleet_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

COMMIT;

-- ============================================================================
--  Rollout order matters
--
--  Apply this and deploy the API FIRST, then release the nodes. The API accepts
--  both the old and the new register payload, so an un-upgraded node keeps
--  registering; but /connect is served from `offers`, which only an upgraded node
--  sends. Between the two steps a node that has not re-registered yet has an empty
--  offers array and /connect returns 503 for its users — so do not leave a gap.
--  Nodes on the `release` channel pick a release up within ~5 minutes.
--
--  Verify the fleet has crossed over:
--    SELECT id, host, profile, jsonb_array_length(offers) AS offers,
--           render_error, last_registered_at
--    FROM vpn_servers WHERE is_active ORDER BY id;
--
--  Every active node should show a profile name and a non-zero offer count.
-- ============================================================================

-- ============================================================================
--  Later, once every node has been on profiles for a while, the pre-profile
--  columns can go. Not part of this migration on purpose: keeping them means a
--  rollback to the previous API build still finds the data it expects.
--
--    ALTER TABLE vpn_servers
--        DROP COLUMN reality_public_key, DROP COLUMN reality_short_ids,
--        DROP COLUMN reality_server_name, DROP COLUMN reality_dest,
--        DROP COLUMN vless_port, DROP COLUMN hysteria_port,
--        DROP COLUMN obfs_password, DROP COLUMN hop,
--        DROP COLUMN olcrtc_provider, DROP COLUMN olcrtc_transport,
--        DROP COLUMN olcrtc_room_id, DROP COLUMN olcrtc_room_key;
-- ============================================================================
