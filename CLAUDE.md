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

HorusAPI is an ASP.NET Core 10 Minimal API for VPN authentication and server management. Clients log in for a **session token**, send it in the `X-Session-Key` header to list servers, and fetch a rendered VPN client config to connect.

### Authentication (session-header, NOT JWT)

Auth is a custom scheme, not JWT (there is no `JwtService`). [Services/Auth Handler/SessionAuthHandler.cs](Services/Auth Handler/SessionAuthHandler.cs) (scheme `SessionHeaderScheme`) reads the `X-Session-Key` header, finds the owning user via `sessions @> ARRAY[@key]` (GIN index `idx_users_sessions`), checks `expires_at > now`, caches the result in `IMemoryCache` (key `session_{key}`), and emits `NameIdentifier`/`Name`/`Role` claims (`Role = "Admin"` when `is_admin`). Session tokens are issued by `UserService.CreateSession` and stored in `users.sessions[]`.

### Endpoint groups

| Group | Auth | Purpose |
|---|---|---|
| `/auth` | anonymous | login, register, logout-others |
| `/servers` | `X-Session-Key` | list, best, connect (rendered config) |
| `/admin` | `X-Session-Key` + `Admin` role (`AdminOnly` policy) | server CRUD, ping, subscription management |
| `/health` | anonymous | liveness check |

`NodeAuthEndpoints` (`/node`) exists in source but is **not** mapped in [Program.cs](Program.cs); nginx only routes `auth|servers|admin|health`.

### Services (all Dapper + NpgsqlConnection, injected via interfaces)

- `UserService` — BCrypt password verify, session token generation, session array management (capped at 10 via SQL slice), register, clear-other-sessions
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

PostgreSQL. Schema in [init.sql](init.sql) (note: `init.sql` is behind the code — the services read `vpn_servers.protocol/hop/obfs_type/obfs_password/auth_password/masquerade_url`, which the committed schema does not yet declare). Key columns:
- `users.is_admin BOOLEAN` — drives the `Role = "Admin"` claim
- `users.expires_at TIMESTAMPTZ` — nullable; NULL = subscription never expires; enforced at auth time in `SessionAuthHandler`
- `users.sessions VARCHAR(64)[]` — bounded to 10 entries; cleared via `ClearOtherSessionsAsync`
- `vpn_servers.protocol` — protocol selector per server (Hysteria2 today; xray-core planned, see below)
- `vpn_servers.auth_password` — per-server secret; today embedded in the rendered config, also intended as the node-agent shared secret (`X-API-PASSWORD`)
- `vpn_servers.masquerade_url` — optional target for admin ping; falls back to `https://{host}`

### Authorization / config rendering

- Admin policy `"AdminOnly"` requires `ClaimTypes.Role = "Admin"`, added when `user.is_admin = true`
- Socks5 defaults for config rendering: `Socks5:Port/Username/Password` in `appsettings.json`
- `/servers/{id}/connect` checks a `subscription_expires_at` claim, but `SessionAuthHandler` does not currently emit that claim — subscription expiry is effectively enforced at auth time instead

### Planned: xray-core migration

Migrating nodes from Hysteria2 to xray-core (VLESS, likely Reality). Key constraint: **xray-core has no per-connection HTTP auth backend** like Hysteria2's `auth.http` — clients are identified by a UUID in the inbound `clients` array, mutated at runtime only via Xray's gRPC HandlerService (`AddInboundUser`/`RemoveInboundUser`). Intended design: a thin **node agent** beside Xray on each node, called by the central API over HTTPS (authenticated with `vpn_servers.auth_password` as `X-API-PASSWORD` — the `ApiConsts` node-agent constants already exist), translating add/remove-user calls into local Xray gRPC and persisting the user set for restart reconciliation. A new `users.vpn_uuid` becomes the per-user identity; `ConfigRenderer` stays (only the template + vars change to a VLESS link / Xray JSON); reconciliation reuses the parallel fan-out in `AdminServerService`.

### EF Core

`AppDbContextcs.cs` (odd filename) is registered but only used for potential tooling; all actual queries use Dapper directly. Connection string key is `"Postgres"` in both.
