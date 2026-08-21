# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build          # build
dotnet run            # run locally (http://localhost:5102 or https://localhost:7083)
docker-compose up     # run full stack (API + PostgreSQL)
dotnet test           # run the test suite (see below)
```

### Tests ([HorusAPI.Tests](HorusAPI.Tests/))

xUnit project (in the solution). Two tiers:

- **Unit** ([HorusAPI.Tests/Unit](HorusAPI.Tests/Unit)) — no DB: `ClientConfigBuilder` link building, `AccountRateLimiter` (3/hour per address). Always run.
- **Integration** ([HorusAPI.Tests/Integration](HorusAPI.Tests/Integration)) — boot the real app in-memory via `WebApplicationFactory<Program>` ([HorusAPI.Tests/Infrastructure](HorusAPI.Tests/Infrastructure)) against a throwaway PostgreSQL database, exercising the full auth flow (register→verify→login, reset, session revocation), `/whoami`, authorization (session/admin), and all the rate-limit layers. `IEmailSender` is replaced with `RecordingEmailSender` so tests can read the code/link that would have been mailed; each test isolates its rate-limit partition with a unique `X-Forwarded-For` IP and a unique e-mail. `Program` is `public partial` so the factory can use it as the entry point.

`PostgresFixture` reads `ConnectionStrings__Postgres` (falls back to `localhost:5432`, user/pw `postgres`), creates an isolated `horus_test_*` DB, applies [init.sql](init.sql), and drops it after. **When no server is reachable the integration tests `Skip` (via `Xunit.SkippableFact`) rather than fail**, so unit tests still pass on a bare checkout. Run the full suite locally with a DB up:

```bash
ConnectionStrings__Postgres="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres" dotnet test
```

### CI/CD ([.github/workflows/deploy.yaml](.github/workflows/deploy.yaml))

`test` job (GitHub-hosted) runs on every push to `main` and every PR: builds Release and runs `dotnet test` against a `postgres:16` service container. The `deploy` job (self-hosted, `deploy.sh`) `needs: test` and only runs on a push to `main` — so a red test blocks the deploy. The Docker image builds `HorusAPI.csproj` explicitly, so the test project never enters the image.

### OpenAPI / API docs (local only)

The spec is generated with the **built-in** `Microsoft.AspNetCore.OpenApi` (not Swashbuckle — its build-time tool version-matches the SDK; Swashbuckle's `SwaggerGen` pins an incompatible `Microsoft.OpenApi` and its `dotnet-getdocument` throws `MissingMethodException`). Wiring is in [Services/OpenApi/OpenApiSetup.cs](Services/OpenApi/OpenApiSetup.cs).

`Microsoft.OpenApi` is pinned to `2.11.0` in the csproj: the generator drags in `2.0.0`, which has a high-severity advisory (GHSA-v5pm-xwqc-g5wc) and is deprecated. **Keep it on the 2.x line** — 3.x makes `IOpenApiMediaType.Example` read-only, which breaks the built-in generator's XML-comment source generator. `dotnet list package --vulnerable --include-transitive` should stay clean.

- **Build-time file**: every `dotnet build` writes `openapi/HorusAPI.json` (via `Microsoft.Extensions.ApiDescription.Server`; `OpenApiDocumentsDirectory` in the csproj). Git-ignored, skipped inside the Docker build (`DOTNET_RUNNING_IN_CONTAINER`). Nothing is published.
- **Development only**: `MapOpenApi` serves `/openapi/v1.json` and `Swashbuckle.AspNetCore.SwaggerUI` (UI-only package, no `Microsoft.OpenApi` dep) renders it at `/swagger`. Both are behind `app.Environment.IsDevelopment()`, so a Production container returns 404 and nginx never routes them.
- A document transformer sets the two `ApiKey` security schemes (`SessionKey` = `X-Session-Key`, `NodePassword` = `X-API-PASSWORD`); an operation transformer attaches the right one per endpoint from its authorization metadata (`IAllowAnonymous`/`IAuthorizeData`), linking the reference to `context.Document` so it serializes.

## Architecture

HorusAPI is an ASP.NET Core 10 Minimal API for VPN authentication and server management. Clients log in for a **session token**, send it in the `X-Session-Key` header to list servers, and fetch a rendered VPN client config to connect.

### Authentication (session-header, NOT JWT)

Auth is a custom scheme, not JWT (there is no `JwtService`). [Services/Auth Handler/SessionAuthHandler.cs](Services/Auth Handler/SessionAuthHandler.cs) (scheme `SessionHeaderScheme`) reads the `X-Session-Key` header, finds the owning user via `sessions @> ARRAY[@key]` (GIN index `idx_users_sessions`), checks `expires_at > now`, caches the result in `IMemoryCache` (key `session_{key}`), and emits `NameIdentifier`/`Name`/`Role` claims (`Role = "Admin"` when `is_admin`). Session tokens are issued by `UserService.CreateSession` and stored in `users.sessions[]`.

### Endpoint groups

| Group | Auth | Purpose |
|---|---|---|
| `/auth` | anonymous | login, register, verify, resend-code, reset-request, reset-check, reset-confirm, logout-others |
| `GET /servers` | `X-Session-Key` | ping candidates: least-loaded-with-capacity, one per country ([ServerEndpoints](Endpoints/ServerEndpoints.cs)) |
| `POST /servers/select` | `X-Session-Key` | reserve/move the caller to a node (auto-picks when `server_id` omitted) |
| `GET /servers/connect` | **anonymous** (session in header **or** `?key=`) | header → JSON `{server,vless[],hysteria2,olcrtc}`; `?key=` → base64 subscription (vless+hysteria2) ([ConnectEndpoints](Endpoints/ConnectEndpoints.cs)) |
| `/admin` | `X-Session-Key` + `Admin` role (`AdminOnly` policy) | server CRUD, ping, subscription (grant = reserve slot, cancel = release) |
| `/whoami` | `X-Session-Key` | egress IP as the API sees it + caller account state |
| `/health` | anonymous | liveness check |

`/node` is mapped in [Program.cs](Program.cs); nginx routes `auth|servers|admin|health|node|whoami` (see [nginx/locations.conf](nginx/locations.conf)) — `/servers/connect` rides the `servers` route, distinct from the site's own `/connect` **page**.

### Server selection, binding & slot reservation

The connection model is split into **selection** and **connection**, and every user is bound to exactly one node. See [docs/connection-model.md](docs/connection-model.md) — the contract the app + node teams build against.

- **Identity**: `users.vpn_uuid` is the stable per-user id (VLESS `id` + node client label). It never changes; only its server binding moves.
- **Binding = reservation**: `users.current_server_id` is the reserved slot; `vpn_servers.reserved_count` counts bound users and is what capacity is measured on (`reserved_count = max_clients` → full). `vpn_servers.current_load` is now the *live* online count from node telemetry (display only).
- [ReservationService](Services/ReservationService.cs) (classic Dapper, **not** `[DapperAot]`) owns reserve/move/release in a single transaction with `FOR UPDATE SKIP LOCKED`, so parallel purchases can't oversell. `EnsureReservedAsync` auto-picks; `SelectAsync` binds/moves; `ReleaseAsync` frees. Node (de)provisioning + session-cache eviction ([SessionCacheOps](Services/Auth Handler/SessionCacheOps.cs)) are done by the caller *after* the commit.
- **Purchase**: admin `PUT …/subscription` reserves first → `409 no_capacity` when every node is full (so a subscription can't be sold with no seats). `DELETE …/subscription` releases the slot + de-provisions the node.
- **`/connect` is node-free on the hot path**: provisioning happens at reserve/select; a normal connect reads one row and builds strings. The node persists its user set and reconciles xray from it on restart.
- **Node protocol is keyed by `vpn_uuid`** (not e-mail): control `POST /users {uuid}` / `DELETE /users/{uuid}`; telemetry `/node/events` events carry `uuid`, and `online_count` drives `current_load`.

### Landing page & client downloads (nginx only — the API is not involved)

The site is a single self-contained [nginx/html/index.html](nginx/html/index.html): a
`dc-runtime` template (`<x-dc>` markup + a `class Component extends DCLogic` script)
rendered by React from unpkg. It is **`COPY`ed into the nginx image**, so a change to
it only reaches the server when that image is rebuilt (`docker compose up -d --build
nginx`) — restarting the container re-runs the old image, which looks exactly like
browser caching but isn't. `deploy.sh` (from [install.sh](install.sh)) therefore
builds `vpn-api nginx`.

Clients download from us, never from github.com. [nginx/sync-releases.sh](nginx/sync-releases.sh)
runs inside the nginx container (start-up + every `RELEASE_SYNC_INTERVAL`), picks the
newest non-draft release of `RELEASE_REPO` via the GitHub API (`/releases`, not
`/releases/latest` — the current build is a **pre-release**, which that endpoint
hides), downloads the Windows/Android assets into the `downloads` volume, verifies
them against the release's `SHA256SUMS.txt`, and only then flips
`/var/www/downloads/current` to the new directory. nginx serves that symlink at
`/download/`, so `/download/Horus-win-x64.msi` is always the newest build and
`/download/latest.json` describes it (version, sizes, checksums). The page uses the
manifest for labels only — the hrefs are static, so downloads survive a failed fetch.

### Email confirmation & password reset

Registration is two-step. `POST /auth/register` creates the account **unverified** (`users.email_verified = FALSE`), mails a 6-digit code from `no-reply@mail.{DOMAIN}`, and answers `202 {status:"unverified"}` — it never returns a session. `POST /auth/verify {email, code}` flips `email_verified` and returns a session (login for an unverified account is refused with `403 code=email_unverified`). `POST /auth/resend-code {email}` re-issues a code. All of this lives in [Services/AccountService.cs](Services/AccountService.cs) + [Endpoints/AuthEndpoints.cs](Endpoints/AuthEndpoints.cs).

Codes are stored as `sha256("{userId}:{code}")` in `email_verifications` (one row per user, upserted; dies after 5 wrong attempts or 15 min). Reset tokens are stored as `sha256(token)` in `password_resets` (single-use, 60 min). `POST /auth/reset-request {email}` always answers `202 {status:"sent"}` regardless of whether the address exists (no account enumeration) and mails a link to `{PublicUrl}/reset?token=…`. The static reset form ([nginx/html/reset.html](nginx/html/reset.html)) validates the token via `GET /auth/reset-check?token=` then posts to `POST /auth/reset-confirm {token, password}`, which sets the new hash, **wipes every session** (evicting their `IMemoryCache` entries) and marks the email verified.

`IEmailSender` = [Services/EmailSender.cs](Services/EmailSender.cs) — plain `System.Net.Mail.SmtpClient` to the `mailserver` (Postfix) container, no auth/TLS (internal hop). Set `Mail__Enabled=false` to log codes/links instead of sending (local `dotnet run` default via env). Never throws — a dead mail server must not fail registration or reveal address existence.

### Rate limiting (layered — [Services/RateLimiting/RateLimitPolicies.cs](Services/RateLimiting/RateLimitPolicies.cs))

`AddHorusRateLimiting()` registers named policies + a `GlobalLimiter` chain; `UseForwardedHeaders` runs **before** `UseRateLimiter` so partitions key on the real client IP (IPv6 bucketed by /64). Rejections return `429 code=rate_limited` with `Retry-After`.

- **Mail routes** (register, resend-code, reset-request; tagged `RateLimitPolicies.Email`) carry all four spec'd layers: per-IP 3/min + 15/hour (global chain, gated by `IsMailRoute` reading endpoint metadata), global 500/hour (the named `email` policy), and **per-account 3/hour per e-mail** via the singleton `IAccountRateLimiter` applied inside the handlers — the layer that survives IP rotation (targeted-harassment defence). The per-account quota is charged uniformly on the anti-enumeration routes (resend/reset) but only on actual send for register (so username-taken retries don't burn it).
- **Other policies**: `login` (sliding 15/5min per IP), `verify` (10/min per IP, guards code/token guessing — the DB attempt counter is the real defence), `session` (120/min per user, keyed off the id prefix of the session token since the limiter runs pre-auth), `admin` (60/min per admin), `node` (300/min per node credential). A 120/min per-IP baseline covers every route including static 404s.

### Services (all Dapper + NpgsqlConnection, injected via interfaces)

- `UserService` — BCrypt password verify, session token generation, session array management (capped at 10 via SQL slice), register (returns `CreateUserResult` distinguishing username-taken vs email-taken by constraint name), clear-other-sessions
- `AccountService` — email-verification codes and password-reset tokens (both stored hashed), password update with session revocation + cache eviction
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
- `users.is_admin BOOLEAN` — drives the `Role = "Admin"` claim
- `users.email_verified BOOLEAN` — gate for login; created `FALSE`, flipped by `/auth/verify`. Idempotent upgrade backfills pre-existing rows as `TRUE` (grandfathered) then resets the default to `FALSE`
- `users.expires_at TIMESTAMPTZ` — nullable; NULL = never expires (free/admin); a past value blocks access (`403 subscription_expired`)
- `users.vpn_uuid UUID` — stable per-user identity (VLESS id + node label); unique index `idx_users_vpn_uuid`; backfilled + `NOT NULL` on upgrade
- `users.current_server_id INT` — the node the user is bound to (their reserved slot); NULL = unbound
- `users.sessions VARCHAR(64)[]` — bounded to 10 entries; cleared via `ClearOtherSessionsAsync` and wiped on password reset
- unique partial index `users_email_lower_key` on `lower(email)` (where non-empty) — one account per address; `CreateUserAsync` maps its `23505` to email-taken
- `email_verifications` (PK `user_id`) / `password_resets` (PK `token_hash`) — hashed single-purpose secret rows, FK `ON DELETE CASCADE`
- `vpn_servers.reserved_count INT` — bound-user count; capacity is measured on this (`= max_clients` → full). `idx_servers_available (country, reserved_count) WHERE is_active` backs selection
- `vpn_servers.current_load INT` — live online count from `/node/events` (display only, not capacity)
- `vpn_servers.auth_password` — per-node shared secret, sent to the agent as `X-API-PASSWORD`
- `vpn_servers.reality_*` / `olcrtc_*` / ports — reported by the node via `/node/register`
- `vpn_servers.masquerade_url` — optional target for admin ping; falls back to `https://{host}`

### Authorization / config rendering

- Admin policy `"AdminOnly"` requires `ClaimTypes.Role = "Admin"`, added when `user.is_admin = true`
- Socks5 defaults for config rendering: `Socks5:Port/Username/Password` in `appsettings.json`
- `/servers/{id}/connect` checks a `subscription_expires_at` claim, but `SessionAuthHandler` does not currently emit that claim — subscription expiry is effectively enforced at auth time instead

### xray-core node model (central side implemented)

The central-API side of the xray model is **in place** (see the selection/binding section above and [docs/connection-model.md](docs/connection-model.md)): `users.vpn_uuid` is the per-user identity, the node agent is called over HTTPS (auth `vpn_servers.auth_password` = `X-API-PASSWORD`) with `POST /users {uuid}` / `DELETE /users/{uuid}`, and links are built by [ClientConfigBuilder](Services/ClientConfigBuilder.cs) (VLESS/Reality + Hysteria2 + olcRTC object; base64 subscription for third-party). **Node-side work** (translate add/remove-user into Xray gRPC `AddInboundUser`/`RemoveInboundUser`, persist the user set, reconcile on restart, keep olcRTC room params current) is the node team's, tracked in the same doc.

### Data access

**Dapper only** — every query uses `Dapper` over a raw `NpgsqlConnection` (opened per call via `new NpgsqlConnection(cfg.GetConnectionString("Postgres"))`). There is no `DbContext` and **no EF Core**: the unused `Npgsql.EntityFrameworkCore.PostgreSQL` / `Microsoft.EntityFrameworkCore.Design` packages were removed, and `Npgsql` is now a direct dependency (previously transitive via the EF provider). The schema is hand-managed in [init.sql](init.sql), not via migrations. Dapper suits this codebase because the queries lean on PostgreSQL-specific features EF maps poorly — array columns + GIN `@>` containment (`users.sessions`), `array_append` with a cap-and-slice, `ON CONFLICT` upserts, `RETURNING`, and multi-statement atomic batches.

**Conventions** (keep queries short and mapping robust):
- `DefaultTypeMap.MatchNamesWithUnderscores = true` is set once in [Program.cs](Program.cs), so snake_case columns map to members ignoring underscores/case — no per-column aliases needed (except where the member name genuinely differs, e.g. `ServerRow.server_id` still needs `id AS server_id`).
- Map straight to the result type — `conn.QueryAsync<T>(sql)` — never `QueryAsync` (dynamic) + a hand-written `.Select(r => new T(...))` projection.
- Large column lists live in a `private const string …Columns` next to the query (e.g. `VpnServerService.ConnectColumns`, `AdminServerService.AdminColumns`).
- **Dapper.AOT** generates command/materializer code at compile time (compile-time column↔member diagnostics, no reflection). Opted in **per class** with `[DapperAot]` on `VpnServerService`, `AdminServerService`, `UserService`, `SessionAuthHandler`; interceptors are enabled via `<InterceptorsNamespaces>` in the csproj. `AccountService`, `NodeService`, and `ReservationService` are deliberately **left classic** (no attribute): AccountService reads the `sessions` array as `string[]` (DAP037 rejects a scalar array — it reads via the `User` type instead); NodeService’s events are simple executes; **ReservationService uses explicit transactions + `FOR UPDATE`**, which is safest on classic Dapper. Note the analyzer runs project-wide regardless of opt-in, so a scalar array read anywhere still trips DAP037.
- The `Dapper.AOT` package ships a **runtime** assembly the generated interceptors call into — reference it normally (do **not** `PrivateAssets=all`, or `dotnet publish` drops `Dapper.AOT.dll` and every intercepted query throws `FileNotFoundException`). The `DAP005` notice it raises in the test project is silenced there via `<NoWarn>`.
