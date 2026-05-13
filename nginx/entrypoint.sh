#!/bin/sh
set -e

DOMAIN="${DOMAIN}"
EMAIL="${CERTBOT_EMAIL}"
STAGING="${CERTBOT_STAGING:-0}"
CERT_DIR="/etc/letsencrypt/live/${DOMAIN}"

# ── Validate required variables ───────────────────────────────
if [ -z "$DOMAIN" ] || [ -z "$EMAIL" ]; then
    echo "[error] DOMAIN and CERTBOT_EMAIL must be set in .env"
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

# ── Render the nginx config template ─────────────────────────
# envsubst replaces ${DOMAIN} only; all other nginx $variables are left intact.
envsubst '${DOMAIN}' \
    < /etc/nginx/nginx-template.conf \
    > /etc/nginx/conf.d/default.conf

# Remove the base nginx default server so our config is the only one active.
rm -f /etc/nginx/conf.d/nginx-default.conf

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

# ── Start nginx (replaces this shell — forwards signals correctly) ─
exec nginx -g "daemon off;"
