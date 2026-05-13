# HorusAPI — Deployment Guide

HorusAPI is an ASP.NET Core 10 VPN authentication and server management API. It runs behind an Nginx reverse proxy that automatically obtains and renews Let's Encrypt TLS certificates.

---

## Architecture

```
Internet
    │
    ▼  :80 (ACME challenge + HTTP→HTTPS redirect)
    ▼  :443 (HTTPS, TLS terminated here)
  nginx  ←── Let's Encrypt (auto-obtained on first start, auto-renewed)
    │
    ├─ /auth/* /servers/* /admin/* /health
    │       ↓  http://vpn-api:8080 (Docker-internal only)
    │    vpn-api
    │       ↓  postgres:5432 (Docker-internal only)
    │    postgres
    │
    └─ /* → static placeholder page
```

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Docker + docker-compose | v20+ |
| A domain name | A record must point to this server's **public IP** |
| Ports 80 and 443 open | Both are needed: 80 for ACME challenge, 443 for HTTPS |

---

## 1. Configure environment variables

```bash
cp .env.example .env
```

Edit `.env` — the minimum required values:

```env
# Your public domain (A record must already point here)
DOMAIN=vpn.example.com

# Email for Let's Encrypt alerts
CERTBOT_EMAIL=admin@example.com

# Leave 0 for real certs; set 1 to test without hitting LE rate limits
CERTBOT_STAGING=0

POSTGRES_PASSWORD=<strong-random-password>
Jwt__Secret=<run: openssl rand -base64 64>
```

> **Tip:** Test cert acquisition first with `CERTBOT_STAGING=1`. Staging issues an untrusted cert so your browser will warn, but it confirms the whole flow works without consuming real LE quota. Switch to `0` once confirmed.

---

## 2. Start the stack

```bash
docker-compose up -d --build
```

On first start, nginx automatically:
1. Spins up a temporary HTTP server on port 80 to serve the ACME challenge
2. Asks Let's Encrypt to issue a certificate for `${DOMAIN}`
3. Shuts down the temporary server and starts the full nginx with HTTPS

This takes **20–60 seconds**. Watch progress with:

```bash
docker-compose logs -f nginx
```

You should see:
```
[certbot] No certificate found for vpn.example.com. Obtaining one from Let's Encrypt...
[certbot] Certificate obtained. Restarting nginx with HTTPS...
```

---

## 3. Verify the deployment

```bash
# Health check
curl https://vpn.example.com/health
# → {"status":"ok","time":"..."}
```

---

## 4. Create the first admin account

Register a user:

```bash
curl -X POST https://vpn.example.com/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<strong-password>","email":"admin@example.com"}'
# → HTTP 201 Created
```

Grant admin rights:

```bash
docker-compose exec postgres psql -U horus -d horus \
  -c "UPDATE users SET is_admin = TRUE WHERE username = 'admin';"
```

---

## 5. Add VPN servers

Get a JWT first:

```bash
TOKEN=$(curl -sX POST https://vpn.example.com/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<your-password>"}' \
  | grep -o '"token":"[^"]*' | cut -d'"' -f4)
```

Add a server:

```bash
curl -X POST https://vpn.example.com/admin/servers \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "EU-FI-01",
    "country": "Finland",
    "city": "Helsinki",
    "host": "srv1.example.com",
    "max_clients": 50,
    "obfs_password": "<random-string>",
    "masquerade_url": "https://srv1.example.com"
  }'
```

---

## Certificate renewal

Renewal is fully automatic. The nginx container runs a background check every 12 hours. Certbot only acts when the certificate is within 30 days of expiry and reloads nginx immediately after renewal — no downtime.

To check the cert expiry date:

```bash
docker-compose exec nginx \
  openssl x509 -enddate -noout -in /etc/letsencrypt/live/${DOMAIN}/fullchain.pem
```

Certificates are stored in the `letsencrypt` Docker named volume and survive container restarts and rebuilds.

---

## API reference

| Endpoint | Auth | Description |
|---|---|---|
| `POST /auth/login` | — | Password or session login → JWT |
| `POST /auth/register` | — | Create account |
| `POST /auth/logout-others` | JWT | Revoke all other sessions |
| `GET /servers` | JWT | List available servers |
| `GET /servers/best` | JWT | Best available server |
| `GET /servers/{id}/connect` | JWT | Rendered Hysteria2 YAML config |
| `GET /admin/servers` | JWT + admin | List all servers (incl. inactive) |
| `POST /admin/servers` | JWT + admin | Add server |
| `DELETE /admin/servers/{id}` | JWT + admin | Remove server |
| `POST /admin/servers/ping` | JWT + admin | Ping all servers |
| `PUT /admin/users/{id}/subscription` | JWT + admin | Set subscription expiry |
| `DELETE /admin/users/{id}/subscription` | JWT + admin | Clear subscription |
| `GET /health` | — | Liveness probe |

Rate limit on `/auth/*`: **10 requests per minute per IP**.

---

## Troubleshooting

**`[certbot] No certificate found...` — hangs or fails**
- Verify the A record for `${DOMAIN}` resolves to this server's public IP: `dig +short ${DOMAIN}`
- Confirm ports 80 and 443 are open: `curl http://${DOMAIN}` from another machine
- Check Let's Encrypt rate limits: you get 5 failed validations per domain per hour. Use `CERTBOT_STAGING=1` while debugging.

**`DOMAIN and CERTBOT_EMAIL must be set`**
- Make sure `.env` exists and both variables are set (not just in `.env.example`)

**`Jwt:Secret is not configured`**
- Check that `Jwt__Secret` is present in `.env` and is at least 64 characters

**Database connection refused**
- Wait a few seconds after `docker-compose up` — vpn-api waits for the postgres healthcheck
- Verify `POSTGRES_PASSWORD` is the same in both env and the postgres service
