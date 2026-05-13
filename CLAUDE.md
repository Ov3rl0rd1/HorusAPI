# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build          # build
dotnet run            # run locally (http://localhost:5102 or https://localhost:7083)
docker-compose up     # run full stack (API + PostgreSQL)
```

No test project exists. Swagger UI is available at `/swagger` in development.

## Architecture

HorusAPI is an ASP.NET Core 10 Minimal API for VPN authentication and server management. Clients authenticate for a JWT, use it to list servers, and fetch a rendered Hysteria2 YAML config to connect.

### Endpoint groups

| Group | Auth | Purpose |
|---|---|---|
| `/auth` | anonymous | login, register, logout-others |
| `/servers` | JWT | list, best, connect (rendered config) |
| `/admin` | JWT + `admin` role | server CRUD, ping, subscription management |
| `/health` | anonymous | liveness check |

### Services (all Dapper + NpgsqlConnection, injected via interfaces)

- `UserService` — BCrypt password verify, session array management (capped at 10 via SQL slice), register, clear-other-sessions
- `JwtService` — HS256 tokens; `expires_at` nullable (null = never expires); adds `role=admin` claim when `user.is_admin`
- `VpnServerService` — available servers, best servers (capacity-filtered), connect data
- `AdminServerService` — full server CRUD, parallel HTTP HEAD ping (named `"ping"` HttpClient), subscription set/clear
- `ConfigRenderer` (static) — two-pass template renderer: resolves `#???varname…#???` conditional blocks first, then `#varname` substitutions

### Config template language (`ApiConsts.CONFIG_TEMPLATE`)

```
#varname             → substituted with vars[varname] (empty string if missing)
#???varname
...block...
#???                 → block included only when vars[varname] is non-null/non-empty
```

`ConfigRenderer.Render(template, vars)` in [Services/ConfigRenderer.cs](Services/ConfigRenderer.cs) implements this. The connect endpoint builds the `vars` dictionary from `ConnectData` fields plus `Socks5:*` config values.

### Database

PostgreSQL. Schema in [init.sql](init.sql). Key columns:
- `users.is_admin BOOLEAN` — drives `role=admin` JWT claim
- `users.expires_at TIMESTAMPTZ` — nullable; NULL = subscription never expires
- `users.sessions VARCHAR(64)[]` — bounded to 10 entries; cleared via `ClearOtherSessionsAsync`
- `vpn_servers.auth_password` — Hysteria2 server auth token embedded in the rendered config
- `vpn_servers.masquerade_url` — optional target for admin ping; falls back to `https://{host}`

### JWT / Authorization

- `Jwt:*` settings in `appsettings.json` (override via `Jwt__Secret` env var in Docker)
- `subscription_expires_at` custom claim — if present and in the past, `/servers/{id}/connect` returns 403
- Admin policy `"AdminOnly"` requires `ClaimTypes.Role = "admin"`, added when `user.is_admin = true`
- Socks5 defaults for config rendering: `Socks5:Port/Username/Password` in `appsettings.json`

### EF Core

`AppDbContextcs.cs` (odd filename) is registered but only used for potential tooling; all actual queries use Dapper directly. Connection string key is `"Postgres"` in both.
