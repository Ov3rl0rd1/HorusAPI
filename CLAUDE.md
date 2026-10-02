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
- **Integration** ([HorusAPI.Tests/Integration](HorusAPI.Tests/Integration)) — boot the real app in-memory via `WebApplicationFactory<Program>` ([HorusAPI.Tests/Infrastructure](HorusAPI.Tests/Infrastructure)) against a throwaway PostgreSQL database, exercising the full auth flow (register→verify→login, reset, session revocation), `/whoami`, authorization (session/admin), and all the rate-limit layers. `IEmailSender` is replaced with `RecordingEmailSender` so tests can read the code/link that would have been mailed, and `INodeNotifier` with `RecordingNodeNotifier` (records add/remove per host instead of calling agents that do not exist; a host starting `down-` answers like an unreachable agent); each test isolates its rate-limit partition with a unique `X-Forwarded-For` IP and a unique e-mail. `Program` is `public partial` so the factory can use it as the entry point.

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
| `/auth` | anonymous | login, register, verify, resend-code, change-email, reset-request, reset-check, reset-confirm, logout-others |
| `GET /servers` | `X-Session-Key` | ping candidates: least-loaded-with-capacity, one per country ([ServerEndpoints](Endpoints/ServerEndpoints.cs)) |
| `POST /servers/select` | `X-Session-Key` | reserve/move the caller to a node (auto-picks when `server_id` omitted) |
| `GET /servers/connect` | **anonymous** (session in header **or** `?key=`) | header → JSON `{server,vless[],hysteria2,olcrtc}`; `?key=` → base64 subscription (vless+hysteria2) ([ConnectEndpoints](Endpoints/ConnectEndpoints.cs)) |
| `/billing` | `X-Session-Key` | `plans`, `checkout` (recurring/one-time), `subscription`, `cancel`, `referral` (own partner code/earnings, invited discount) ([BillingEndpoints](Endpoints/BillingEndpoints.cs)) |
| `POST /payments/{provider}/webhook` | **anonymous** (secret checked in-adapter, idempotent) | payment provider callbacks |
| `/admin` | `X-Session-Key` + `Admin` role (`AdminOnly` policy) | server CRUD, ping, **node detail** (`GET /servers/{id}`), **evacuate/activate a node**, **move one user off a node**, user search, comp subscription (grant = reserve slot, revoke = release), **a user's monthly traffic** (`GET /users/{u}/traffic`), **plan catalogue** (list/create/edit), closed-plan grants (grant/list/revoke), refunds, promo codes, **referral partners** (appoint/edit/switch off, detail, payouts), `gate` (204 — nginx gates `/panel` on it) |
| `/whoami` | `X-Session-Key` | egress IP as the API sees it + caller account state |
| `/health` | anonymous | liveness check |

`/node` is mapped in [Program.cs](Program.cs); nginx routes `auth|servers|admin|health|node|whoami|billing|payments` (see [nginx/locations.conf](nginx/locations.conf)) — `/servers/connect` rides the `servers` route, distinct from the site's own `/connect` **page**. Billing/subscriptions are documented in [docs/payments.md](docs/payments.md).

### Server selection, binding & slot reservation

The connection model is split into **selection** and **connection**, and every user is bound to exactly one node. See [docs/connection-model.md](docs/connection-model.md) — the contract the app + node teams build against.

- **Identity**: `users.vpn_uuid` is the stable per-user id (VLESS `id` + node client label). It never changes; only its server binding moves.
- **Binding = reservation**: `users.current_server_id` is the reserved slot; `vpn_servers.reserved_count` counts bound users + pending holds and is what capacity is measured on. Two limits: **`max_reservations`** is the HARD cap (`reserved_count = max_reservations` → full; buy/select refused), **`max_clients`** is a SOFT "recommended" threshold (~1.5× smaller) used only for client load display — reservations keep succeeding past it up to the hard cap. `vpn_servers.current_load` is the *live* online count from node telemetry (display only).
- [ReservationService](Services/ReservationService.cs) (classic Dapper, **not** `[DapperAot]`) owns reserve/move/release in a single transaction with `FOR UPDATE SKIP LOCKED`, so parallel purchases can't oversell. `EnsureReservedAsync` auto-picks; `SelectAsync` binds/moves; `ReleaseAsync` frees. Node (de)provisioning + session-cache eviction ([SessionCacheOps](Services/Auth Handler/SessionCacheOps.cs)) are done by the caller *after* the commit.
- **Purchase**: admin `PUT …/subscription` reserves first → `409 no_capacity` when every node is full (so a subscription can't be sold with no seats). `DELETE …/subscription` releases the slot + de-provisions the node.
- **`/connect` is node-free on the hot path**: provisioning happens at reserve/select; a normal connect reads one row and builds strings. The node persists its user set and reconciles xray from it on restart.
- **Node protocol is keyed by `vpn_uuid`** (not e-mail): control `POST /users {uuid, usage?}` / `DELETE /users/{uuid}` (answers `{removed, usage}`); telemetry `/node/events` events carry `uuid`, `online_count` drives `current_load`, and `usage[]` carries users' months (see "Monthly traffic" below).

### Billing, subscriptions & access model ([Services/Billing](Services/Billing), [docs/payments.md](docs/payments.md))

Access is an **entitlement**, not the old NULL check. A user has access iff `is_admin OR
expires_at > now()`; `users.expires_at` is a **cache** recomputed from the `subscriptions`
table (source of truth) by [EntitlementService](Services/Billing/EntitlementService.cs). The
gate lives in [AccessPolicy](Services/Billing/AccessPolicy.cs) (`ServerEndpoints.IsExpired` and
`/servers/connect` call it). **A NULL/past `expires_at` now means "no active subscription"** —
a freshly registered non-admin user has no access until they buy (this closed the old
"NULL = free forever" hole). Money is **whole rubles** everywhere (no minor units).

- **Provider abstraction**: the core talks only to [`IPaymentProvider`](Services/Billing/PaymentContracts.cs) + normalised types; [PlategaProvider](Services/Billing/PlategaProvider.cs) is the only adapter (chosen in `Program.AddPaymentProvider`). The webhook decoder `PlategaWebhook.Parse` is pure and unit-tested — it folds Platega's three callback shapes into one `PaymentEvent`.
- **[BillingService](Services/Billing/BillingService.cs)** is the money engine: checkout, cancel, idempotent webhook handling (`webhook_events`), refund. Classic Dapper + explicit transactions; node (de)provisioning + session-cache eviction run **after** commit, like the reservation flows.
- **[PlanService](Services/Billing/PlanService.cs)** owns the catalogue/promo/grants: plans a user may buy (public + granted non-public "для своих"), promo validation, and admin grant/comp/promo-CRUD.
- **Capacity holds**: checkout charges a seat to `vpn_servers.reserved_count` immediately via `slot_holds` (TTL `Payments:HoldMinutes`), so a full fleet fails the buy *before* payment. [ReservationService](Services/ReservationService.cs) gained `HoldSlotAsync`/`ConfirmHoldAsync`/`ReleaseHoldAsync`/`SweepExpiredHoldsAsync`; because a hold uses the same `reserved_count`, every existing candidate/select/pick query is unchanged. [BillingSweeperService](Services/Billing/BillingSweeperService.cs) (hosted) releases expired holds + fails stale pending payments.
- **Admin catalogue** (`/admin/plans`, `PlanService.ListPlansAdminAsync`/`CreatePlanAsync`/`UpdatePlanAsync`):
  no DELETE — `is_active = false` takes a plan off sale (a promo tied to a plan would be cascaded
  away with it). `code` is fixed once created and unique case-insensitively (checkout looks up
  `lower(code) … LIMIT 1`). **`kind`/`interval_*` are frozen (`409 plan_in_use`) while any
  subscription on the plan is `pending`/`active`/`past_due`**: renewals extend the period by the
  plan's *current* interval (`BillingService.PeriodEndFrom`) while the provider charges on the
  schedule the subscription was created with. The price is free to change — it applies to new
  purchases. Body rules are the pure `PlanService.Validate` (unit-tested).
- **Closed-plan grants**: `plan_grants.expires_at = NULL` is **бессрочно**; `GET /admin/users/{u}/grants`,
  `GET /admin/plans/{id}/grants`, `DELETE /admin/users/{u}/grants/{code}`. Revoking takes away the
  right to buy only — a subscription already bought on the plan is untouched.
- **Promo caveat**: promos are percent-off, first-charge-only → they apply to **one-time** buys; a promo on a recurring plan is refused (`promo_not_applicable`) because Platega recurring charges a fixed amount every period.
- **Provider reconciliation** (polling for missed webhooks) is a documented follow-up, not yet implemented.
- **Recurring activation confirms the checkout's payment row.** It used to stay `pending`, and
  `BillingSweeperService` then marked a paid subscription's first payment `failed`; activation now
  sets it `confirmed` (also from `failed`, for an activation arriving after the sweep).

### Referral partners ([ReferralService](Services/Billing/ReferralService.cs), [ReferralTests](HorusAPI.Tests/Integration/ReferralTests.cs))

A **partner** is any user the admin appoints (`PUT /admin/users/{u}/referral {code, discount_percent,
reward_percent, is_active, note}` — no self-service). Their **code** reaches customers as a link
(`{PublicUrl}/login?mode=register&ref=CODE`, sent by `/auth/register` as `referral_code`) or typed
into the checkout's **promo field** — the two share one namespace, enforced both ways (`409 code_taken`).

- **Binding** (`users.referred_by`, `AttachAsync`): once, first code wins, **new customers only**
  (no confirmed/refunded payment, no non-manual live subscription — comp grants don't count),
  never yourself. One `UPDATE … WHERE referred_by IS NULL AND NOT customer`, so racing codes can't
  both win. Refusals at checkout are `400 referral_not_applicable` with `self_referral` /
  `already_referred` / `existing_customer` in the message; at sign-up an unknown code never blocks —
  the 202 says `referral: "applied" | "invalid"`.
- **Discount**: `discount_percent` (0–90) on **every** purchase while the partner is active —
  recurring included, because a permanent discount is exactly what Platega's fixed recurring
  amount carries (unlike first-charge promos). It does not stack with a promo: the bigger applies,
  and a losing promo is not redeemed. `payments.referrer_id` records the partner considered.
- **Reward**: `reward_percent` (0–100) of what was **actually paid**, rounded down, at the partner's
  current percent and only while active. Accrued when money lands: one-time confirm (**inside**
  its transaction — `ReferralService.AccrueInAsync`), recurring activation and each renewal
  (best effort + `LogError`: those webhooks are not idempotent on replay without a provider period
  end, so a failed accrual must not become a retry that extends access twice). `referral_rewards.source`
  is the idempotency key: `payment:{id}` for one-time, `subscription:{id}:{period end date}` for a
  recurring period — so a provider reporting the first charge both as activation and as a charge
  pays once. **Refund/chargeback** (`RevokeAndReleaseAsync`) reverses the purchase's latest reward.
- **Payouts** are manual; `POST /admin/users/{u}/referral/payouts {amount, note}` records one and is
  refused above the balance (`409 payout_exceeds_balance`). Balance = accrued − payouts (can go
  negative after a reversal of already-paid money). Switching a partner off (`{is_active:false}`)
  stops the discount on new checkouts and new accruals; existing recurring prices stay (the
  provider holds them) and earned money stays. No DELETE — rewards and payouts are money history.
- Schema: `referral_partners` (PK `user_id`, unique `lower(code)`), `users.referred_by/referred_at`
  (`ON DELETE SET NULL`), `payments.referrer_id`, `referral_rewards`, `referral_payouts`.
  Existing DB: `migrations/003_referrals.sql`. The site: `?ref=` is remembered in `localStorage`
  (`horus.ref`) by `login.html`, the pay page's code field shows for subscriptions too, and the
  cabinet shows a partner tile from `GET /billing/referral`.

### Monthly traffic follows the user ([TrafficService](Services/TrafficService.cs), [TrafficTests](HorusAPI.Tests/Integration/TrafficTests.cs))

Speed and monthly allowances are enforced on the node, by xray's TariffService — but xray's
ledger is per node, and moving a user used to hand them a fresh month there. The month now
lives **here, per user**: `traffic_usage (user_id, month DATE, total_bytes, olcrtc_bytes,
server_id, updated_at)`, PK `(user_id, month)`, `ON DELETE CASCADE`. Existing DB:
`migrations/004_traffic_usage.sql`.

- **Figures are absolute month-to-date** (`"yyyy-MM"` + bytes), never deltas, so recording is
  `ON CONFLICT … GREATEST(old, new)` per counter: a repeated, late or stale report — an old node
  still holding a user who has moved — can never lower or double a month. Bad lines (not a
  month, negative, unknown uuid) are skipped, not failed.
- **Three ways in, all through [NodeNotifier](Services/NodeNotifier.cs) / NodeService:**
  `/node/events` `usage[]` (the node's periodic report); `POST /users` carries the month so far
  (`CurrentMonthAsync`) so the new node restores it into xray before the first byte; and the
  old node's `DELETE` answer brings back the bytes since its last report. A lookup failure
  provisions without `usage` (logged) — a user who cannot connect is worse.
- **Order of a move matters.** The user's own `POST /servers/select` (`ReprovisionAsync`)
  removes the old node first, so the new one is handed the full month. The admin move
  (`AfterMoveAsync`) provisions the new node first — evacuation must not wait on an
  unreachable old node — and so **re-provisions it after the old node answers**; the node only
  ever raises its counter *up to* a figure (never adds it on top), so that is safe.
- Nodes that predate this send no `usage` and answer DELETE with 204; both are fine.
- The panel's user card shows the current month (`GET /admin/users/{u}/traffic`, last 6 months).

### Evacuating a node

`POST /admin/servers/{id}/evacuate` moves every user off a node and takes it out of rotation;
`POST /admin/servers/{id}/activate` puts it back. Written for an address being blocked — the
node is healthy and reachable from everywhere except where the users are.

Two things about it are load-bearing. **`is_active = false` is set before anything is read**:
the auto-picker only considers active nodes, and a node that has just had a seat freed is the
least-loaded one, so otherwise the fleet hands each user straight back to the node they are
being moved off. And **`SelectAsync` returning Ok is not enough** — it keeps the existing
binding when nothing in the fleet has room, which is right for an ordinary move and a lie
here, so the result is compared against the source id and counted as `stayed` rather than
`moved`.

**One user** is moved with `POST /admin/servers/{id}/users/{userId}/evacuate {server_id?}`, which
leaves the node in rotation. Both flows go through `ReservationService.MoveOffAsync(user, from, to?)`:
it refuses (`409 not_on_server`) when the user is no longer on `from` — a stale admin view must
not take someone off the node they have since chosen — and its auto-pick **excludes the source
explicitly**, because a node still in rotation with a seat just freed is the least-loaded one.
A full fleet is `409 no_capacity` there, never `SelectAsync`'s "keep the binding and say Ok".
The after-commit part (`AdminEndpoints.AfterMoveAsync`) is shared too: provision the new node,
de-provision the old one, evict the user's cached sessions.

The source node is read with `IAdminServerService.GetNodeEndpointAsync`, **not**
`IVpnServerService.GetConnectDataAsync` — that one only returns active nodes, and evacuation has
just deactivated this one, so de-provisioning used to be silently skipped. `INodeNotifier`
returns `false` rather than throwing, and a `false` from provisioning the new node is a
reported problem.

Node calls happen after the commit, like every other reservation flow, and a failure there
does not roll the binding back: the database is the truth and the node reconciles its user set
from it. De-provisioning the old node is best effort on purpose — it is very likely the
unreachable one. The answer is an `EvacuationReport` rather than a 204, and **re-running is
how a partial evacuation is finished**: the users who moved are no longer bound there, so a
second pass sees only what is left.

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

### Admin panel ([nginx/panel/](nginx/panel/))

`/panel` is a plain page over the `/admin/*` API: nodes (evacuate/activate, profiles, ping,
add, a **detail card** with offers and bound users, **move one user**), user search + comp
+ **traffic this month**,
move off a node and closed-plan grants (select + «бессрочно»), **tariffs** (list, create, edit,
take off sale, who a closed plan is open to), payments + refunds, promo codes, **partners**
(referral programme: appoint from the user card or the list, card with link, invited customers,
rewards incl. reversals, payouts; switch on/off). **Everyone but an
admin gets the same 404 as any missing path** — byte for byte, headers included.

nginx decides with `auth_request` → `GET /admin/gate` (204 for an admin). A browser
navigation cannot send `X-Session-Key`, so [js/session.js](nginx/html/js/session.js) mirrors
the session into a `horus_panel` cookie scoped to `Path=/panel` (`SameSite=Lax` so a link
from Telegram still works) and rewrites it on every page load — an admin who signed in
before the panel existed only has to open any page of the site once. The gate turns the
API's 401 into 403 before `auth_request` sees it, because on a 401 `auth_request` copies
`WWW-Authenticate` from the raw upstream headers, where `proxy_hide_header` does not reach.

The files are **outside the public root** (`/usr/share/nginx/panel`), so a lost gate makes
them unreachable rather than public. The markup is free to change: JS binds only through
`data-*` attributes (the contract is at the top of `bind.js`), never classes, and the copy
lives in the markup. `ask` in `bind.js` is the one place that shows dialogs (currently
`confirm`/`prompt`) — replace it there when a styled dialog exists. `data-options` marks a
`<select>` whose options JS writes (options with `data-fixed` stay), `setForm` fills a form for
editing, and `REFUSALS` in `bind.js` maps API refusal codes to Russian text with the way out.

### Monitoring ([monitoring/](monitoring/))

Separate compose project, meant for its **own small VPS** — a watcher on the machine it
watches cannot report that machine's death. VictoriaMetrics (storage + scraping + vmui) +
vmalert + Alertmanager (native `telegram_configs`); no Prometheus, no Grafana. ~350 MB RAM,
~500 MB disk for 30 days of the whole fleet.

**Pull, not push.** VM scrapes every server over the TLS port it already publishes, at
`/metrics/host` (node-exporter), `/metrics/containers` (cadvisor) and `/metrics/agent` (the
node agent), all behind nginx HTTP basic auth built from `METRICS_TOKEN` — one fleet-wide,
read-only value, deliberately **not** any node's `NODE_API_PASSWORD`. Empty token renders
`return 404;`, so a rebuilt server never starts publishing telemetry on its own. The payoff
is that a dead server is `up == 0`, an alert; a push design would just go quiet, which is
indistinguishable from healthy.

Targets live in `monitoring/targets/*.yml` (file_sd, re-read every minute — adding a node
restarts nothing). Dashboards are vmui custom dashboards in `monitoring/dashboards/`.
Alert rules: `infra.yml` (host), `horus.yml` (xray, **per-user limits** — not applied, xray without
TariffService, server monthly traffic ≥ 80 % / exhausted —, olcrtc rooms, profile render,
certificate expiry **and name coverage**, container limits) and `blocking.yml`.

**Detecting an RKN IP block needs a vantage point inside Russia** — no check from a foreign
server can see it. Two signals. The free one is inferred from metrics already collected:
a node whose xray answers and whose scrape succeeds, with zero users online *and at least
three online within the last six hours* — that last clause is what separates a block from a
quiet night or a fresh node. The direct one is `monitoring/ru-probe/`, a blackbox-exporter
and vmagent on a small Russian VPS that **push** TCP-connect results; it pushes rather than
serving so nothing has to be opened on it, and it knows only addresses and ports, because a
box in that jurisdiction should be worthless if seized. `NodeBlockedFromRussia` fires only
when the probe fails **and we can still reach the node** — otherwise it is an outage, which
`ServerUnreachable` already covers. The probe pushes through the same HTTPS as the web UI
(`POST /api/v1/write`, basic auth with `PROBE_PASSWORD`) — VictoriaMetrics' own port is never
published: it has no authentication, and the same port serves vmui and the delete API.

**Web access** ([monitoring/WEB-ACCESS.md](monitoring/WEB-ACCESS.md)): Caddy terminates HTTPS
for `MONITOR_DOMAIN` (Let's Encrypt, automatic) and asks `gate` (stdlib Python,
[monitoring/gate/gate.py](monitoring/gate/gate.py), tests alongside) about every request via
`forward_auth`. Two ways in: a Telegram Mini App (initData HMAC with the bot token, user id in
`TG_ALLOWED_USERS`; the menu button is set per allowed chat) or `ACCESS_KEY` (form, or
`Authorization: Bearer`). Only an **allow-list of read-only VictoriaMetrics paths** is proxied
— VM runs `delete_series` on a plain GET — and any path with `..`, `//` or `\` is refused
first, because Caddy matches the cleaned path but forwards the raw one. The session is a signed
`__Host-` cookie (key derived from bot token + `ACCESS_KEY` + `SESSION_SALT`), `SameSite=None;
Partitioned` only inside Telegram Web's iframe.

`cadvisor` runs here by default and is **opt-in on nodes** (`COMPOSE_PROFILES=containers`):
50-80 MB is affordable on this host and is not on a 700 MB/1-core node, where xray's health
already comes from `/metrics/agent` and an OOM kill shows up in `node_vmstat_oom_kill`.

Logs are not in this stack yet — VictoriaLogs + fluent-bit is the documented follow-up.

### Email confirmation & password reset

Registration is two-step. `POST /auth/register` creates the account **unverified** (`users.email_verified = FALSE`), mails a 6-digit code from `no-reply@mail.{DOMAIN}`, and answers `202 {status:"unverified"}` — it never returns a session. `POST /auth/verify {email, code}` flips `email_verified` and returns a session (login for an unverified account is refused with `403 code=email_unverified`). `POST /auth/resend-code {email}` re-issues a code. All of this lives in [Services/AccountService.cs](Services/AccountService.cs) + [Endpoints/AuthEndpoints.cs](Endpoints/AuthEndpoints.cs).

**An unfinished registration is recoverable.** Logging in to an unverified account no longer
dead-ends: `/auth/login` answers `403 code=email_unverified` *plus* a **pending ticket**
(`pending_logins`, sha256-stored, 30 min, one per account), the masked address, and the
resend countdown. The ticket is not a session — `SessionAuthHandler` reads `users.sessions[]`
and never looks at `pending_logins` — and it opens exactly three things: `/auth/resend-code`,
`/auth/change-email`, `/auth/verify`. `verify` and `resend-code` take either an address or a
ticket; the ticket path exists because someone who signed in with their **username** was
never told which address to quote, and what they were shown is masked.

`POST /auth/change-email {pending_token, email}` corrects a typo before confirmation. It
**deletes the outstanding code**, so one mailed to the old address can never confirm the new
one, and refuses an address the account already has (otherwise "changing" it to itself would
reset the cooldown).

**Resend cooldown** (`AccountService.ResendCooldown`, 60 s) sits on top of the 3/hour
per-address quota and is what the button counts down from. The two paths order their checks
differently **on purpose**: by ticket, cooldown is checked *before* the quota and the true
remaining seconds come back (the caller owns the account, nothing can leak). By address the
quota is charged for **every** address first, and the response always states the *full*
cooldown — if the quota were charged only on an actual send, an unknown address would
eventually 429 while a real one in cooldown never would, and that difference is an account
oracle.

**Three numbers the confirmation screen needs, and the rule they share.** The `403` from `/auth/login` carries `codeExpiresInSeconds` (0 when no live code is pending, so the screen shows nothing rather than counting down from a code that is gone) alongside the resend countdown — both read off one `email_verifications` row. `/auth/verify` returns
`attemptsLeft` on a wrong code, and a spent hourly quota now answers `429` with `Retry-After`
(from the fixed-window lease) so the screen can say "next one at 14:35" instead of "later".
Both follow the same rule as the cooldown: **the number goes back only to a caller who proved
they own the account.** `attemptsLeft` is therefore ticket-path only — an unregistered address
reports `0` while a real unconfirmed one reports `4`, which would answer "is there a pending
registration here". `/auth/register` returns a `pendingToken` (the caller just created the
account, so it is theirs); **`/auth/resend-code` must never fill that field in** — it is
anonymous and answers for any address, so a ticket there would hand anyone any account. There
is a test for each of these three.

The site's confirmation screen ([nginx/html/login.html](nginx/html/login.html) `#state-verify`,
styles in [css/auth.css](nginx/html/css/auth.css), logic in [js/auth.js](nginx/html/js/auth.js))
holds its copy in the markup: JS only clears `hidden` on one `.notice` block and fills
`[data-slot]`. `.auth-code.is-stale` is "this code is dead" (expired or five wrong guesses) —
the field dims and the primary button becomes "prislat noviy kod", obeying the same cooldown as
the resend link so the only available action is never presented as unavailable.

[UnverifiedSweeperService](Services/UnverifiedSweeperService.cs) deletes abandoned unverified
accounts (`Accounts:UnverifiedTtlHours`, default 168; `0` disables) — they otherwise hold a
username and an address against unique indexes forever, so the person who mistyped cannot even
sign up again. **Every FK into `users` is `ON DELETE CASCADE`**, so the SQL guards in
`DeleteStaleUnverifiedAsync` are deliberately broader than the invariants require: no session,
no `current_server_id`, no `expires_at`, and no row in `subscriptions`/`payments`/`slot_holds`/
`plan_grants`/`promo_redemptions`/`referral_partners`. Each guard has a test.

Codes are stored as `sha256("{userId}:{code}")` in `email_verifications` (one row per user, upserted; dies after 5 wrong attempts or 15 min). Reset tokens are stored as `sha256(token)` in `password_resets` (single-use, 60 min). `POST /auth/reset-request {email}` always answers `202 {status:"sent"}` regardless of whether the address exists (no account enumeration) and mails a link to `{PublicUrl}/reset?token=…`. The static reset form ([nginx/html/reset.html](nginx/html/reset.html)) validates the token via `GET /auth/reset-check?token=` then posts to `POST /auth/reset-confirm {token, password}`, which sets the new hash, **wipes every session** (evicting their `IMemoryCache` entries) and marks the email verified.

`IEmailSender` = [Services/EmailSender.cs](Services/EmailSender.cs) — plain `System.Net.Mail.SmtpClient` to the `mailserver` (Postfix) container, no auth/TLS (internal hop). Set `Mail__Enabled=false` to log codes/links instead of sending (local `dotnet run` default via env). Never throws — a dead mail server must not fail registration or reveal address existence.

### Rate limiting (layered — [Services/RateLimiting/RateLimitPolicies.cs](Services/RateLimiting/RateLimitPolicies.cs))

`AddHorusRateLimiting()` registers named policies + a `GlobalLimiter` chain; `UseForwardedHeaders` runs **before** `UseRateLimiter` so partitions key on the real client IP (IPv6 bucketed by /64). Rejections return `429 code=rate_limited` with `Retry-After`.

- **Mail routes** (register, resend-code, reset-request; tagged `RateLimitPolicies.Email`) carry all four spec'd layers: per-IP 3/min + 15/hour (global chain, gated by `IsMailRoute` reading endpoint metadata), global 500/hour (the named `email` policy), and **per-account 3/hour per e-mail** via the singleton `IAccountRateLimiter` applied inside the handlers — the layer that survives IP rotation (targeted-harassment defence). The per-account quota is charged uniformly on the anti-enumeration routes (resend/reset) but only on actual send for register (so username-taken retries don't burn it).
- **Other policies**: `login` (sliding 15/5min per IP), `verify` (10/min per IP, guards code/token guessing — the DB attempt counter is the real defence), `session` (120/min per user, keyed off the id prefix of the session token since the limiter runs pre-auth), `admin` (60/min per admin), `node` (300/min per node credential), `connect` (60/min per token), `billing` (20/min per user), `webhook` (240/min per IP — generous so a provider's legit retries are never throttled; the shared secret, not the limiter, is the gate). A 120/min per-IP baseline covers every route including static 404s.

### Services (all Dapper + NpgsqlConnection, injected via interfaces)

- `UserService` — BCrypt password verify, session token generation, session array management (capped at 10 via SQL slice), register (returns `CreateUserResult` distinguishing username-taken vs email-taken by constraint name), clear-other-sessions
- `AccountService` — email-verification codes and password-reset tokens (both stored hashed), password update with session revocation + cache eviction
- `VpnServerService` — available servers, best servers (capacity-filtered), connect data
- `AdminServerService` — full server CRUD, parallel HTTP HEAD ping (named `"ping"` HttpClient), subscription set/clear
- `ConfigRenderer` (static) — two-pass template renderer: resolves `#???varname…#???` conditional blocks first, then `#varname` substitutions
- **Billing** ([Services/Billing](Services/Billing)) — `IEntitlementService` (recompute the `expires_at` cache), `IPlanService` (catalogue/promo/grants/comp), `IBillingService` (checkout/cancel/webhook/refund + hold sweep), `IPaymentProvider`→`PlategaProvider` (pluggable acquirer), `BillingSweeperService` (hosted). All classic Dapper (transactions); `PlategaWebhook`/`AccessPolicy`/`Pricing` are pure + unit-tested

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
- `vpn_servers.reserved_count INT` — bound-user + pending-hold count; capacity is measured on this against **`max_reservations`** (hard cap → full). `max_clients` is a soft/advisory threshold only. `idx_servers_available (country, reserved_count) WHERE is_active` backs selection
- `vpn_servers.current_load INT` — live online count from `/node/events` (display only, not capacity)
- `vpn_servers.auth_password` — per-node shared secret, sent to the agent as `X-API-PASSWORD`
- `vpn_servers.reality_*` / `olcrtc_*` / ports — reported by the node via `/node/register`
- `vpn_servers.masquerade_url` — optional target for admin ping; falls back to `https://{host}`
- `traffic_usage` — a user's monthly traffic across all nodes (PK `user_id, month`); see "Monthly traffic follows the user"

Referral tables: `referral_partners`, `referral_rewards`, `referral_payouts`, plus `users.referred_by`,
`users.referred_at`, `payments.referrer_id` — see "Referral partners" above.

Billing tables (all amounts **whole rubles**; see [docs/payments.md](docs/payments.md)):
- `plans` — tariff catalogue (`code`, `kind` recurring/one_time, `interval_*`, `amount`, `is_public`). Ships empty; seeded by the operator
- `plan_grants` — a user's access to a non-public plan ("для своих")
- `subscriptions` — **source of access truth**; `status` (pending/active/past_due/canceled/failed/comp), `current_period_end`, `provider_ref`, `server_id`. `users.expires_at` is recomputed from this
- `payments` — checkout intents + outcome; `provider_ref` correlates webhooks; `hold_id` → `slot_holds`
- `subscription_charges` — one row per recurring charge (`provider_txn_id` unique = idempotency)
- `refunds` — admin-initiated refunds
- `webhook_events` — raw callbacks + idempotency guard (`provider`, `provider_event_id` unique)
- `promo_codes` / `promo_redemptions` — percent promos + per-user redemption tracking
- `slot_holds` — pending checkout slot reservation (one per user, TTL); counted in `vpn_servers.reserved_count`

### Authorization / config rendering

- Admin policy `"AdminOnly"` requires `ClaimTypes.Role = "Admin"`, added when `user.is_admin = true`
- Socks5 defaults for config rendering: `Socks5:Port/Username/Password` in `appsettings.json`
- Access/subscription expiry is enforced by [AccessPolicy](Services/Billing/AccessPolicy.cs) reading `users.expires_at` (the entitlement cache) — `ServerEndpoints.IsExpired` and `/servers/connect` both gate on it; admins bypass. See the billing section above

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
