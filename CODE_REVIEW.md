# Code Review — Session Auth & Routes Rewrite

**Scope:** `git diff HEAD~2..HEAD` — commits `6607202` *Fix auth handler and rewrite routes logic* + `488ee82` *Add const for session key header*
**Date:** 2026-07-05
**Method:** 10 independent finder angles (5 correctness, 3 cleanup, altitude, conventions), deduplicated, verified against the working tree. 15 findings survived.

---

## TL;DR

The session-auth rework has a cluster of real security bugs centered on one line — `SessionAuthHandler.cs:71`. The `expires_at > now && sessions.Contains(...)` condition only decides whether to **cache** the user; the user is returned (and authenticated) regardless. Combined with the fact that nothing ever evicts `session_{key}` cache entries, this means: logged-out/stolen sessions keep working, expired subscriptions (including expired admins) keep authenticating, and admin revocations don't take effect. Separately, the new `{userId}.{random}` token format silently logs out every existing user on deploy, and a fresh install from the committed `init.sql` can't serve `/servers/*` at all.

**Highest-leverage fix:** make `GetSessionAsync` actually enforce expiry/validity on the return path (not just the cache write), and evict `session_{key}` entries in `ClearOtherSessionsAsync` and the admin subscription endpoints — that resolves findings #1, #2, and #10 together.

---

## Findings (ranked most-severe first)

### 1. 🔴 Revoked sessions keep authenticating — cache is never invalidated

**File:** `Services/UserService.cs:117` (+ `SessionAuthHandler.cs:73-81`, `AuthEndpoints.cs:96`) · **Security** · CONFIRMED

`ClearOtherSessionsAsync` (invoked by `POST /auth/logout-others`) and the admin subscription set/clear endpoints update Postgres but never evict the `session_{key}` entries in `IMemoryCache`.

**Failure scenario:** User suspects a stolen session and calls `POST /auth/logout-others` → DB `sessions[]` is reduced to the current key, but the thief's cached entry (`SlidingExpiration` 12 h, **renewed on every request**, `AbsoluteExpiration` = the old `expires_at`, possibly months away) keeps passing auth as long as it is used once per 12 h. Same mechanism: an admin shortening/revoking a subscription or demoting an admin has no effect on already-cached sessions — the demoted admin keeps the `Admin` role claim.

### 2. 🔴 Expired subscriptions still authenticate — the expiry check only gates caching

**File:** `Services/Auth Handler/SessionAuthHandler.cs:71` · **Security / Correctness** · CONFIRMED

The `user.expires_at > DateTime.UtcNow` check only gates the cache write — line 84 (`return user`) is unconditional — contradicting CLAUDE.md's *"enforced at auth time in SessionAuthHandler"*.

**Failure scenario:** User with `expires_at` = yesterday sends a valid session key → `GetUserFromPostgresAsync` matches (the SQL has no `expires_at` filter), the check merely skips caching, `HandleAuthenticateAsync` returns Success → 200 on `/servers/best` and `/auth/logout-others`; an expired `is_admin` user retains full `/admin` access. Only `/servers/connect` independently returns 403.

### 3. 🔴 Deploying this commit logs out the entire fleet

**File:** `Services/Auth Handler/SessionAuthHandler.cs:90` · **Availability** · CONFIRMED

The new `{userId}.{base64}` parse (`Split('.')` requiring exactly 2 parts) rejects every pre-deploy session token (64-char dotless base64), with no fallback or migration.

**Failure scenario:** Any client holding a pre-upgrade token sends it after deploy → `Split` yields 1 part → `null` → `AuthenticateResult.Fail` → 401 on every endpoint until re-login. The dead old-format strings also keep occupying slots in the 10-entry sessions cap until trimmed by new logins.

### 4. 🔴 Fresh install cannot serve `/servers/*` — schema missing columns

**File:** `init.sql:15` · **Correctness** · CONFIRMED

`init.sql` was edited in this diff (index removal) but `vpn_servers` still lacks the `protocol`, `obfs_type`, `obfs_password`, and `hop` columns that `GetBestServersAsync`/`GetConnectDataAsync` SELECT. (The gap is acknowledged in CLAUDE.md but left unfixed while touching the file.)

**Failure scenario:** Fresh `docker-compose up` runs `init.sql` → `GET /servers/best` or `/servers/connect` → `PostgresException 42703 'column "protocol" does not exist'` → swallowed by the blanket catch → every request returns 503 "Database error."

### 5. 🔴 Deactivated/banned accounts retain full access — `is_active` never checked

**File:** `Services/UserService.cs:49` · **Security** (pre-existing, in touched file) · CONFIRMED

`AuthenticateAsync` verifies only the BCrypt password, and `SessionAuthHandler`'s SQL filters only on `id` + session containment — `users.is_active` is never consulted.

**Failure scenario:** Admin sets `is_active = FALSE` for an abusive user → the user still logs in with the correct password, `CreateSession(user.id)` mints a fresh token, and every `/servers/*` endpoint (including rendered VPN configs) keeps working.

### 6. 🟠 Unthrottled DB-load amplification via invalid session keys

**File:** `Services/Auth Handler/SessionAuthHandler.cs:69` · **Security / Performance** · CONFIRMED

Negative lookups are never cached, and the `/servers` group has no rate limiter (only `/auth` has the `"auth"` fixed-window).

**Failure scenario:** A client (or attacker) hammers `GET /servers/best` with `X-Session-Key: 1.<random base64>` → passes the Split/TryParse prefilter, always misses the cache (only successful users are cached), and runs one fresh `NpgsqlConnection` + SELECT per request with no throttle, saturating the pool. Consider a short-TTL negative cache and a limiter on `/servers`.

### 7. 🟠 `POST /auth/logout-others` returns 400/415 unless the client sends a pointless `{}`

**File:** `Endpoints/AuthEndpoints.cs:82` (+ `Models/ApiRecords.cs:6`) · **Correctness** · CONFIRMED

`LogoutOthersRequest` is now an empty record but is still bound as a required `[FromBody]` parameter.

**Failure scenario:** A client updated for the new contract sends the request with only the `X-Session-Key` header and no body (there is nothing left to send) → 415 without `Content-Type` or 400 on the empty body, instead of 204. Drop the parameter and the record entirely.

### 8. 🟠 Route rewrite breaks deployed clients and removes server choice

**File:** `Endpoints/ServerEndpoints.cs:26` (+ `DEPLOY.md:170-172`, `Models/ApiRecords.cs:4`) · **Breaking change** · CONFIRMED

`GET /servers/` and `GET /servers/{id}/connect` now 404 (nginx still proxies `/servers`, DEPLOY.md still documents the old routes), `/connect` always returns the single least-loaded server, and `LoginResponse` dropped the `username` field.

**Failure scenario:** A shipped client lists `/servers/best` (20 servers with country/city), the user picks Japan, but the only connect route returns whatever server has the lowest `current_load` (e.g. Finland) — the listing offers a choice the API can no longer honor, and until `current_load` updates, every connecting client herds onto the same node. Old clients calling the documented `/servers/3/connect` get 404.

### 9. 🟠 Rendered config auth string changed from `username:session` to bare session

**File:** `Endpoints/ServerEndpoints.cs:66` · **Integration** · PLAUSIBLE

Nothing in this repo validates either format — any node-side Hysteria2 auth backend still expecting `username:session` will reject all connections from freshly rendered configs.

**Failure scenario:** A node's auth validator splits the auth string on `:` expecting a username/session pair; a client fetches a new config whose auth is `12.xYz…==` → node-side parse/lookup fails → VPN handshake rejected for every user on a new config. *Confirm what the nodes' `auth.http` expects before shipping.*

### 10. 🟠 Never-expiring users are never cached — the cache is dead for the lifetime tier

**File:** `Services/Auth Handler/SessionAuthHandler.cs:71` · **Performance** · CONFIRMED

`expires_at = NULL` means "never expires", but the lifted nullable comparison `null > DateTime.UtcNow` is false, so those users always miss the cache.

**Failure scenario:** Every authenticated request from a lifetime user opens a Postgres connection and runs `SELECT * FROM users`; DB load scales with request rate. Intended condition: `user.expires_at == null || user.expires_at > DateTime.UtcNow`, with a fixed relative expiration for the null case.

### 11. 🟡 `/connect` fetch path: double enumeration, mismatched null guard, two queries, TOCTOU

**File:** `Endpoints/ServerEndpoints.cs:40-46` · **Correctness-fragility / Efficiency** · CONFIRMED

`bestServers.FirstOrDefault()` is enumerated twice (re-running the lazy Dapper `Select`), the null guard checks a *different expression* than the `bestServer` value passed on, and two sequential queries read the same row at different instants — all replaceable by one `SELECT … ORDER BY current_load LIMIT 1` service call.

**Failure scenario:** Today only nullability warnings flag the guard mismatch; if `GetBestServersAsync` ever returns an unbuffered/live sequence, the guard can pass while `bestServer` is null → `NullReferenceException` at `server.id`, misreported as 503 "Database error." by the blanket catch. A server deactivated between the two queries yields a confusing 404 right after `/best` returned it.

### 12. 🟡 `Context.Items["User"]` written with a string literal, read via `ApiConsts.UserHttpContext`

**File:** `Services/Auth Handler/SessionAuthHandler.cs:43` · **Cleanup (fails open)** · CONFIRMED

The handler writes the literal `"User"` while both readers (`ServerEndpoints.cs:31`, `AuthEndpoints.cs:87`) use the constant added in this same diff — they match only by string coincidence.

**Failure scenario:** If the const's value is ever changed, endpoints read `null` with no compile error: `/auth/logout-others` returns 401 for authenticated users, and `/servers/connect`'s subscription-expiry check silently passes (`user?.expires_at == null` → guard skipped) — the expiry check **fails open**.

### 13. 🟡 Session-token format defined in two files, with a dead `key` local as a trap

**File:** `Services/Auth Handler/SessionAuthHandler.cs:97` (+ `UserService.cs:18-23`) · **Cleanup** · CONFIRMED

The `{userId}.{random}` format is composed in `UserService.GenerateSession` and re-parsed by hand in the auth handler with no shared helper; the parse leaves an unused `string key = parts[1]` (the SQL correctly binds the **full** `sessionKey`).

**Failure scenario:** The unused `key` implies the SQL should match on the suffix alone; a maintainer "fixing" the query to `@key` would break every login (`sessions[]` stores the full composite string). Any format evolution edited on the generate side compiles cleanly but makes every parse return null at runtime. A `SessionToken` type owning format + parse removes both traps.

### 14. 🟡 Dead code left behind by the rewrite

**File:** `Endpoints/NodeAuthEndpoint.cs:15` (+ `Services/VpnServerService.cs:18`) · **Cleanup** · CONFIRMED

`MapNodeAuthEndpoints` is a gutted shell (empty rate-limited `/node` group, unused `UsernameRegex`, stale usings) never called from `Program.cs`; `GetAvailableServersAsync` + `ServerListItem` have zero callers since the `/servers/` list endpoint was deleted.

**Failure scenario:** The shell invites re-populating `/node` by copy-pasting from `AuthEndpoints` (recreating the duplication this diff just removed), and the dead SQL that SELECTs `obfs_password` must be mirrored through every future schema change (e.g. the xray migration) despite never running. Delete the file, the interface slot, and the method.

### 15. 🟡 CLAUDE.md rewritten in this diff contradicts the code it ships with

**File:** `CLAUDE.md:21` · **Conventions / Docs** · CONFIRMED

Five stale claims in the same-diff doc rewrite:
- cites the GIN index `idx_users_sessions` that this diff **deletes** from `init.sql`;
- describes `/servers/{id}/connect` checking a `subscription_expires_at` claim (route renamed to `/servers/connect`, constant deleted);
- lists a `/servers` list endpoint that was removed;
- claims `expires_at` is "enforced at auth time" (it only gates caching — see finding #2);
- omits the new `SessionKey` claim and the `Context.Items["User"]` contract.

**Failure scenario:** The project's own instruction file misdescribes the auth mechanism, endpoint table, and enforcement point of the exact subsystem this diff changed, so the next contributor (or Claude session) works from a false model.

---

## Refuted / out of scope

- **GIN index drop is safe** — the auth query now leads with the primary key (`WHERE id = @UserId AND …`), and `ClearOtherSessionsAsync` filters on the unique-indexed `username`; no remaining query scans `sessions[]` unindexed.
- **`NoResult()` on missing header behaves correctly** — protected endpoints still get a 401 challenge; anonymous endpoints are unaffected.
- **Crafted `otherId.key` tokens cannot impersonate other users** — the containment check matches the full stored string, which embeds the real owner's id.
- **`Context.Items` is populated on both cache-hit and DB paths**, and claims are built identically on both.
- **No dangling references** to the deleted `SUBSCRIPTION_EXPIRES_AT`/`SESSION_ID` constants or old record shapes.
- **Local build failure is unrelated** — caused by untracked `HorusAPI.AppHost`/`HorusAPI.ServiceDefaults` leftovers whose `obj/` artifacts get swept into the root project's compile glob; nothing from the diff'd files.
