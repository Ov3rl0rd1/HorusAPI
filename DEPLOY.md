# HorusAPI — Deployment Guide

HorusAPI is an ASP.NET Core 10 VPN authentication and server management API. It runs **HTTPS-only on port 443** and uses Let's Encrypt certificates managed by the co-located Hysteria2 server.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Docker + docker-compose | v20+ |
| Hysteria2 server | Must have ACME (Let's Encrypt) configured and certs already issued |
| A domain name | With an A record pointing to this server's IP |

---

## 1. Locate Hysteria2 TLS certificates

Hysteria2 stores ACME certificates in the directory defined by `acme.dir` in its config (default: `/etc/hysteria`). After Hysteria2 has successfully obtained a certificate, the directory contains:

```
/etc/hysteria/
  ca.crt          ← CA certificate (not used by HorusAPI)
  server.crt      ← server certificate (fullchain)
  server.key      ← private key
```

Some setups use certbot instead, in which case certs are at:
```
/etc/letsencrypt/live/<your-domain>/
  fullchain.pem
  privkey.pem
```

Note the directory path — you will set it as `CERT_DIR` in the next step.

> **Permission note:** The private key file must be readable by the Docker container process (UID 65534 in the `vpnapi` system account). Run `chmod o+r /etc/hysteria/server.key` on the host if needed, or adjust the file's group ownership.

---

## 2. Configure environment variables

```bash
cp .env.example .env
```

Edit `.env` and fill in every value:

```env
# PostgreSQL
POSTGRES_DB=horus
POSTGRES_USER=horus
POSTGRES_PASSWORD=<strong-random-password>

# JWT — minimum 64 characters
Jwt__Secret=<run: openssl rand -base64 64>
Jwt__Issuer=horus-auth-api
Jwt__Audience=horus-clients
Jwt__ExpiryMinutes=60

# Socks5 proxy defaults embedded in rendered client configs
Socks5__Port=1080
Socks5__Username=<your-socks5-username>
Socks5__Password=<your-socks5-password>

# Directory on the HOST containing fullchain.pem (or server.crt) and privkey.pem (or server.key)
CERT_DIR=/etc/hysteria
```

Then update `docker-compose.yml` certificate path variables to match the actual filenames inside `CERT_DIR`:

```yaml
Kestrel__Certificates__Default__Path:    /certs/server.crt   # or fullchain.pem
Kestrel__Certificates__Default__KeyPath: /certs/server.key   # or privkey.pem
```

---

## 3. Start the stack

```bash
docker-compose up -d --build
```

Docker will:
1. Start PostgreSQL and run `init.sql` to create the schema
2. Build and start HorusAPI, listening on `https://0.0.0.0:443`

Check that both containers are running:

```bash
docker-compose ps
docker-compose logs vpn-api
```

---

## 4. Create the first admin account

Register a user via the API:

```bash
curl -k -X POST https://<your-domain>/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<strong-password>","email":"admin@example.com"}'
# → HTTP 201 Created
```

Grant admin rights directly in the database:

```bash
docker-compose exec postgres psql -U horus -d horus \
  -c "UPDATE users SET is_admin = TRUE WHERE username = 'admin';"
```

---

## 5. Verify the deployment

```bash
# Health check
curl https://<your-domain>/health
# → {"status":"ok","time":"..."}

# Login and obtain a JWT
curl -X POST https://<your-domain>/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<your-password>"}'
# → {"token":"eyJ...","expires_at":"...","username":"admin","session":"..."}

# List servers (requires JWT)
curl https://<your-domain>/servers \
  -H "Authorization: Bearer <token>"
```

---

## 6. Add VPN servers

Use the admin API with the JWT obtained above:

```bash
curl -X POST https://<your-domain>/admin/servers \
  -H "Authorization: Bearer <admin-token>" \
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

## 7. Certificate renewal

Hysteria2 renews Let's Encrypt certificates automatically. After renewal, HorusAPI must be restarted to load the new certificate:

```bash
docker-compose restart vpn-api
```

To automate this, add a cron job on the host (e.g., via `/etc/cron.d/horus-cert-reload`):

```cron
0 3 * * * root docker compose -f /path/to/HorusAPI/docker-compose.yml restart vpn-api
```

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

**Container fails to start with certificate error**
- Verify `CERT_DIR` points to a directory containing the expected PEM files
- Check file permissions: both files must be readable by the container (`chmod o+r`)
- Confirm the filenames in `docker-compose.yml` match the actual files in `CERT_DIR`

**`Jwt:Secret is not configured`**
- Make sure `.env` is present and `Jwt__Secret` is set (minimum 64 characters)

**Database connection refused**
- The API depends on the `postgres` healthcheck; wait a few seconds and check `docker-compose logs postgres`
- Verify `POSTGRES_PASSWORD` matches in both the `postgres` and `vpn-api` service sections
