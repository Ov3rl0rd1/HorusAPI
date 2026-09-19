#!/bin/sh
set -e

DOMAIN="${DOMAIN}"
EMAIL="${CERTBOT_EMAIL}"
STAGING="${CERTBOT_STAGING:-0}"
USE_SSL="${USE_SSL:-1}"
CERT_DIR="/etc/letsencrypt/live/${DOMAIN}"

# ── Validate required variables ───────────────────────────────
# DOMAIN is always needed (server_name); CERTBOT_EMAIL only matters for TLS.
if [ -z "$DOMAIN" ]; then
    echo "[error] DOMAIN must be set in .env"
    exit 1
fi

if [ "$USE_SSL" = "1" ]; then

    if [ -z "$EMAIL" ]; then
        echo "[error] CERTBOT_EMAIL must be set in .env when USE_SSL=1"
        exit 1
    fi

    # ── First-boot certificate acquisition ───────────────────────
    if [ ! -f "${CERT_DIR}/fullchain.pem" ]; then
        echo "[certbot] No certificate found for ${DOMAIN}. Obtaining one from Let's Encrypt..."

        # Start a minimal HTTP-only nginx to serve the ACME challenge.
        # We cannot use the full config yet because the cert does not exist.
        cat > /tmp/acme-nginx.conf << EOF
events {}
http {
    server {
        listen 80;
        server_name _;
        location /.well-known/acme-challenge/ {
            root /var/www/certbot;
        }
        location / {
            return 503 "Obtaining SSL certificate, please try again shortly.";
            add_header Content-Type text/plain;
        }
    }
}
EOF

        nginx -c /tmp/acme-nginx.conf
        sleep 2  # give nginx time to bind

        STAGING_FLAG=""
        if [ "$STAGING" = "1" ]; then
            STAGING_FLAG="--staging"
            echo "[certbot] Using Let's Encrypt STAGING environment (no real cert issued)"
        fi

        certbot certonly \
            --webroot \
            --webroot-path /var/www/certbot \
            --email "$EMAIL" \
            --agree-tos \
            --no-eff-email \
            --non-interactive \
            $STAGING_FLAG \
            -d "$DOMAIN"

        echo "[certbot] Certificate obtained. Restarting nginx with HTTPS..."
        nginx -c /tmp/acme-nginx.conf -s quit
        sleep 1
    fi

fi

# ── Pick the template based on USE_SSL ───────────────────────
if [ "$USE_SSL" = "1" ]; then
    TEMPLATE=/etc/nginx/nginx-template.conf
else
    echo "[entrypoint] USE_SSL=0 — serving plain HTTP on port 80 (no TLS)."
    TEMPLATE=/etc/nginx/nginx-http-template.conf
fi

# ── Render the nginx config template ─────────────────────────
# envsubst replaces ${DOMAIN} only; all other nginx $variables are left intact.
# -- Metrics gate --------------------------------------------------------------
# locations.conf includes this file in every /metrics/* location. Rendered here
# rather than baked into the image because it carries a credential.
#
# Empty METRICS_TOKEN renders a bare `return 404;`: a server that was rebuilt but
# never configured for monitoring must not start publishing its telemetry to
# whoever asks. Failing closed is the only safe default for a file whose entire
# job is to decide who may read.
GATE=/etc/nginx/metrics-gate.conf
if [ -n "${METRICS_TOKEN}" ]; then
    htpasswd -bcB /etc/nginx/metrics.htpasswd horus "${METRICS_TOKEN}" >/dev/null 2>&1
    chmod 640 /etc/nginx/metrics.htpasswd
    cat > "$GATE" <<'GATECONF'
auth_basic           "horus metrics";
auth_basic_user_file /etc/nginx/metrics.htpasswd;
GATECONF
    echo "[entrypoint] metrics enabled at /metrics/{host,containers}"
else
    echo 'return 404;' > "$GATE"
    rm -f /etc/nginx/metrics.htpasswd
    echo "[entrypoint] METRICS_TOKEN is empty - /metrics/* disabled (404)"
fi

envsubst '${DOMAIN}' \
    < "$TEMPLATE" \
    > /etc/nginx/conf.d/default.conf

# Remove the base nginx default server so our config is the only one active.
rm -f /etc/nginx/conf.d/nginx-default.conf

# ── Client release mirror ─────────────────────────────────────
# Pulls the newest release from GitHub into /var/www/downloads so nginx serves
# the installers itself. Runs in the background: an unreachable GitHub must not
# hold up the site, and the first sync moves ~300 MB.
if [ "${RELEASE_SYNC:-1}" = "1" ]; then
    (
        while true; do
            /sync-releases.sh || echo "[releases] sync failed — retrying later"
            sleep "${RELEASE_SYNC_INTERVAL:-3600}"
        done
    ) &
fi

if [ "$USE_SSL" = "1" ]; then
    # ── Background renewal daemon ─────────────────────────────────
    # Checks every 12 hours; certbot only renews when < 30 days remain.
    # The --deploy-hook reloads nginx only when a cert was actually renewed.
    (
        while true; do
            sleep 12h
            echo "[certbot] Running scheduled renewal check..."
            certbot renew \
                --quiet \
                --deploy-hook "nginx -s reload"
        done
    ) &

fi

# ── Start nginx (replaces this shell — forwards signals correctly) ─
exec nginx -g "daemon off;"
