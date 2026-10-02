using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Services.Billing;
using HorusAPI.Services.Auth_Handler;
using Microsoft.Extensions.Caching.Memory;

namespace HorusAPI.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting(RateLimitPolicies.Admin);

        // The site's admin panel (/panel) is gated by nginx's auth_request on this route:
        // 204 lets the file through, and the 401/403 an ordinary visitor gets here is
        // turned into a plain 404 there. The group's policy does the whole check, so the
        // handler has nothing left to decide — which is exactly why it cannot get it wrong.
        group.MapGet("/gate", () => Results.NoContent())
            .Produces(204)
            .WithSummary("204 for an admin session; nginx gates the /panel pages on it");

        // Find a user to act on. The subscription/grant routes below take a username, and
        // payments name users by id — this is how an admin gets from either to the other.
        group.MapGet("/users", async ([FromQuery] string? q, IAdminServerService svc) =>
        {
            if (q?.Length > 128)
                return Results.BadRequest(new ApiError("Query is too long."));

            try { return Results.Ok(await svc.SearchUsersAsync(q)); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<UserAdminItem>>(200)
        .Produces<ApiError>(400)
        .WithSummary("Search users by username, e-mail or id (up to 50; newest first when q is empty)");

        // Ping all servers
        group.MapPost("/servers/ping", async (IAdminServerService svc) =>
        {
            IEnumerable<PingResult> results;
            try { results = await svc.PingAllServersAsync(); }
            catch { return Results.Problem("Ping operation failed.", statusCode: 503); }
            return Results.Ok(results);
        })
        .Produces<IEnumerable<PingResult>>(200)
        .WithSummary("Ping all VPN servers to check masquerade site availability");

        // List all servers (including inactive)
        group.MapGet("/servers", async (IAdminServerService svc) =>
        {
            IEnumerable<ServerAdminItem> servers;
            try { servers = await svc.GetAllServersAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(servers);
        })
        .Produces<IEnumerable<ServerAdminItem>>(200)
        .WithSummary("List all VPN servers including inactive ones");

        // ── xray profiles ────────────────────────────────────────────────────
        // A profile is what a node actually runs (xray/profiles/<name>.json in the node
        // repo). Assigning one here is how a protocol is switched under a block: the
        // node picks the assignment up on its next telemetry post and applies it on its
        // next update tick, with no SSH and no deploy.
        //
        // The profile must already exist on the nodes — this only selects between
        // profiles that have been shipped. A name nothing recognises leaves the node on
        // its previous config and reports render_error, visible below.

        // What every node is running versus what it was told to run.
        group.MapGet("/servers/profiles", async (IAdminServerService svc) =>
        {
            IEnumerable<ServerProfileState> states;
            try { states = await svc.GetProfileStatesAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(states);
        })
        .Produces<IEnumerable<ServerProfileState>>(200)
        .WithSummary("Per-node xray profile state: running vs assigned, hashes, offer count, render errors");

        group.MapGet("/fleet/profile", async (IAdminServerService svc) =>
        {
            FleetProfile fleet;
            try { fleet = await svc.GetFleetProfileAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(fleet);
        })
        .Produces<FleetProfile>(200)
        .WithSummary("The fleet-wide default xray profile");

        // Move the whole fleet. Every node without its own override switches.
        group.MapPut("/fleet/profile", async (
            [FromBody] SetProfileRequest req, IAdminServerService svc) =>
        {
            if (!AdminServerService.IsValidProfileName(req?.profile))
                return Results.BadRequest(new ApiError(
                    "Profile names may contain letters, digits, '-', '_' and '.' only.", "invalid_profile"));

            try { await svc.SetFleetProfileAsync(req?.profile); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Ok(await svc.GetFleetProfileAsync());
        })
        .Produces<FleetProfile>(200)
        .Produces<ApiError>(400)
        .WithSummary("Set the fleet-wide default xray profile (empty clears it)");

        // Override one node — useful for trying a new protocol on a single node first.
        group.MapPut("/servers/{id:int}/profile", async (
            [FromRoute] int id, [FromBody] SetProfileRequest req, IAdminServerService svc) =>
        {
            if (!AdminServerService.IsValidProfileName(req?.profile))
                return Results.BadRequest(new ApiError(
                    "Profile names may contain letters, digits, '-', '_' and '.' only.", "invalid_profile"));

            bool found;
            try { found = await svc.SetServerProfileAsync(id, req?.profile); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (!found) return Results.NotFound(new ApiError("Server not found.", "server_not_found"));

            var states = await svc.GetProfileStatesAsync();
            return Results.Ok(states.FirstOrDefault(s => s.id == id));
        })
        .Produces<ServerProfileState>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .WithSummary("Set one node's xray profile override (empty falls back to the fleet default)");

        // Add new server 
        group.MapPost("/servers", async (
            [FromBody] AddServerRequest req,
            IAdminServerService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.name)    ||
                string.IsNullOrWhiteSpace(req.country) ||
                string.IsNullOrWhiteSpace(req.city)    ||
                string.IsNullOrWhiteSpace(req.host)    ||
                req.max_clients <= 0)
                return Results.BadRequest(new ApiError("name, country, city, host, and max_clients are required."));

            if (req.name.Length > 128 || req.country.Length > 64 ||
                req.city.Length > 64  || req.host.Length  > 256)
                return Results.BadRequest(new ApiError("Field length limit exceeded."));

            if (req.max_reservations is int cap && cap < req.max_clients)
                return Results.BadRequest(new ApiError("max_reservations (hard cap) cannot be below max_clients."));

            if (!string.IsNullOrWhiteSpace(req.masquerade_url) &&
                (!Uri.TryCreate(req.masquerade_url, UriKind.Absolute, out var mUrl) ||
                 (mUrl.Scheme != "https" && mUrl.Scheme != "http")))
                return Results.BadRequest(new ApiError("masquerade_url must be a valid http/https URL."));

            int newId;
            try { newId = await svc.AddServerAsync(req); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Created($"/admin/servers/{newId}", new { id = newId });
        })
        .Produces(201)
        .Produces<ApiError>(400)
        .WithSummary("Add a new VPN server");

        // Remove server
        group.MapDelete("/servers/{id:int}", async (
            [FromRoute] int id,
            IAdminServerService svc) =>
        {
            bool removed;
            try { removed = await svc.RemoveServerAsync(id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return removed
                ? Results.NoContent()
                : Results.NotFound(new ApiError($"Server {id} not found."));
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Remove a VPN server");

        // ── Evacuation ────────────────────────────────────────────────────────────

        // Move everyone off a node and stop it taking new ones. The reason this exists is
        // an address being blocked: the node is healthy, reachable from everywhere except
        // where the users are, and every one of them has to be somewhere else within
        // minutes rather than hours.
        group.MapPost("/servers/{id:int}/evacuate", async (
            [FromRoute] int id,
            IAdminServerService svc,
            IReservationService reservation,
            INodeNotifier       notifier,
            IMemoryCache        cache,
            ILogger<Program>    log) =>
        {
            // First, and before anything is read. The auto-picker only considers active
            // nodes, so until this lands the fleet is free to hand users straight back to
            // the node they are being moved off — and with a seat just freed, it is the
            // least-loaded one, which makes it the pick.
            try
            {
                if (!await svc.SetServerActiveAsync(id, false))
                    return Results.NotFound(new ApiError($"Server {id} not found."));
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            IReadOnlyList<BoundUser> bound;
            NodeEndpoint? source;
            try
            {
                bound = await svc.GetBoundUsersAsync(id);
                // Not the connect-path read: that one only returns active nodes, and this one
                // was just switched off — de-provisioning would silently never happen.
                source = await svc.GetNodeEndpointAsync(id);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            int moved = 0, stayed = 0, failed = 0;
            var problems = new List<string>();

            foreach (var user in bound)
            {
                MoveResult result;
                try { result = await reservation.MoveOffAsync(user.id, id, null); }
                catch (Exception ex)
                {
                    failed++;
                    problems.Add($"{user.username}: {ex.Message}");
                    continue;
                }

                // Left the node on their own between the list being read and now — there is
                // nothing to do, and moving them again would take them off wherever they went.
                if (result.status == MoveStatus.NotOnServer) continue;

                // Nowhere in the fleet had room. The user is still on the node being evacuated,
                // and saying so is the point of the report.
                if (result.status != MoveStatus.Moved)
                {
                    stayed++;
                    continue;
                }

                moved++;

                var problem = await AfterMoveAsync(user.id, user.username, user.vpn_uuid, source,
                    result.serverId!.Value, svc, notifier, cache, log);
                if (problem is not null)
                {
                    failed++;
                    problems.Add(problem);
                }
            }

            log.LogWarning("Evacuated server {Server}: {Moved} moved, {Stayed} stayed, {Failed} with problems",
                id, moved, stayed, failed);

            // Re-running is the intended way to finish a partial evacuation: the users who
            // moved are no longer bound here, so a second pass only sees what is left.
            return Results.Ok(new EvacuationReport(
                id, bound.Count, moved, stayed, failed, problems));
        })
        .Produces<EvacuationReport>(200)
        .Produces<ApiError>(404)
        .WithSummary("Deactivate a node and move every user off it; re-run to retry what is left");

        // Move ONE user off a node, leaving the node in rotation. For the user whose address is
        // the one being blocked, or to rebalance by hand. server_id picks the destination;
        // omitted, it is the least-loaded other active node.
        group.MapPost("/servers/{id:int}/users/{userId:int}/evacuate", async (
            [FromRoute] int id,
            [FromRoute] int userId,
            [FromBody]  MoveUserRequest? req,
            IAdminServerService svc,
            IReservationService reservation,
            INodeNotifier       notifier,
            IMemoryCache        cache,
            ILogger<Program>    log) =>
        {
            User? user;
            NodeEndpoint? source;
            MoveResult result;
            try
            {
                user = await svc.GetUserByIdAsync(userId);
                if (user is null) return Results.NotFound(new ApiError($"User {userId} not found.", "user_not_found"));

                source = await svc.GetNodeEndpointAsync(id);
                result = await reservation.MoveOffAsync(userId, id, req?.server_id);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            switch (result.status)
            {
                case MoveStatus.NotOnServer:
                    return Results.Json(new ApiError($"{user.username} is not on server {id}.", "not_on_server"), statusCode: 409);
                case MoveStatus.SameServer:
                    return Results.BadRequest(new ApiError("The user is already on that server.", "same_server"));
                case MoveStatus.TargetNotFound:
                    return Results.NotFound(new ApiError($"Server {req?.server_id} not found.", "target_not_found"));
                case MoveStatus.TargetInactive:
                    return Results.Json(new ApiError($"Server {req?.server_id} is out of rotation.", "target_inactive"), statusCode: 409);
                case MoveStatus.NoCapacity:
                    return Results.Json(new ApiError(req?.server_id is null
                        ? "No other node has a free seat."
                        : $"Server {req.server_id} is full.", "no_capacity"), statusCode: 409);
            }

            int to = result.serverId!.Value;
            var problem = await AfterMoveAsync(user.id, user.username, user.vpn_uuid, source, to, svc, notifier, cache, log);

            NodeEndpoint? target = null;
            try { target = await svc.GetNodeEndpointAsync(to); } catch { /* the name is cosmetic */ }

            log.LogWarning("Admin moved user {User} off server {From} to {To}", user.username, id, to);

            return Results.Ok(new UserMoveReport(
                user.id, user.username, id, to, target?.name ?? $"#{to}",
                problem is null ? [] : [problem]));
        })
        .Produces<UserMoveReport>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .WithSummary("Move one user off a node (to server_id, or the least-loaded other node); the node stays in rotation");

        // One node in detail: counters, profile state, the offers it serves, and who is on it.
        group.MapGet("/servers/{id:int}", async ([FromRoute] int id, IAdminServerService svc) =>
        {
            ServerDetail? detail;
            try { detail = await svc.GetServerDetailAsync(id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return detail is null
                ? Results.NotFound(new ApiError($"Server {id} not found.", "server_not_found"))
                : Results.Ok(detail);
        })
        .Produces<ServerDetail>(200)
        .Produces<ApiError>(404)
        .WithSummary("One node in detail: capacity counters, profile state, offers, bound users");

        // Put a node back into rotation after an evacuation, or after maintenance.
        group.MapPost("/servers/{id:int}/activate", async (
            [FromRoute] int id,
            IAdminServerService svc) =>
        {
            bool ok;
            try { ok = await svc.SetServerActiveAsync(id, true); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return ok ? Results.NoContent() : Results.NotFound(new ApiError($"Server {id} not found."));
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Put a node back into rotation");

        // Grant/extend a COMP (free, service) subscription. This is the manual "purchase":
        // it reserves a node slot (409 no_capacity when the fleet is full) and writes a
        // comp subscription row (the entitlement source of truth), so access survives the
        // access-cache recompute. For paid tariffs a user checks out via /billing.
        group.MapPut("/users/{username}/subscription", async (
            [FromRoute] string username,
            [FromBody]  SetSubscriptionRequest req,
            IAdminServerService svc,
            IPlanService        plans,
            IReservationService reservation,
            IVpnServerService   servers,
            INodeNotifier       notifier) =>
        {
            User? user;
            try { user = await svc.GetByUsernameAsync(username); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            if (user is null) return Results.NotFound(new ApiError($"User {username} not found."));

            ReserveResult res;
            try { res = await reservation.EnsureReservedAsync(user.id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            if (res.status == ReserveStatus.NoCapacity)
                return Results.Json(new ApiError("No free slots — cannot grant a subscription.", "no_capacity"), statusCode: 409);

            try { await plans.CompAsync(username, req.expires_at.ToUniversalTime()); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (res.newlyReserved)
            {
                ServerRow? server = await servers.GetConnectDataAsync(res.serverId!.Value);
                if (server is not null)
                    await notifier.AddUserAsync(new NodeTarget(server.host, server.auth_password), user.vpn_uuid.ToString());
            }

            return Results.NoContent();
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .WithSummary("Grant/extend a comp subscription and reserve a node slot (409 no_capacity when full)");

        // Revoke a comp subscription: expire manual grants, free the reserved slot, de-provision.
        // Paid (provider) subscriptions are untouched here — use the refund endpoint for those.
        group.MapDelete("/users/{username}/subscription", async (
            [FromRoute] string username,
            IAdminServerService svc,
            IPlanService        plans,
            IReservationService reservation,
            IVpnServerService   servers,
            INodeNotifier       notifier) =>
        {
            User? user;
            try { user = await svc.GetByUsernameAsync(username); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            if (user is null) return Results.NotFound(new ApiError($"User {username} not found."));

            int? previous;
            try
            {
                await plans.RevokeCompAsync(username);
                previous = await reservation.ReleaseAsync(user.id);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (previous is int oldId)
            {
                ServerRow? old = await servers.GetConnectDataAsync(oldId);
                if (old is not null)
                    await notifier.RemoveUserAsync(new NodeTarget(old.host, old.auth_password), user.vpn_uuid.ToString());
            }

            return Results.NoContent();
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Revoke comp access: expire manual grants, free the reserved slot, de-provision the node");

        // ── Grants & comp (для своих) ──────────────────────────────────────────────

        // Grant a user access to a non-public ("для своих") plan so they can buy it.
        group.MapPost("/users/{username}/grant", async (
            [FromRoute] string username,
            [FromBody]  GrantBody req,
            HttpContext ctx,
            IPlanService plans) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User admin) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req?.plan_code))
                return Results.BadRequest(new ApiError("plan_code is required."));

            bool ok;
            try { ok = await plans.GrantAsync(admin.id, username, req.plan_code.Trim(), req.expires_at); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return ok ? Results.NoContent() : Results.NotFound(new ApiError("User or plan not found."));
        })
        .Produces(204)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .WithSummary("Grant a user access to a non-public plan (для своих).");

        // What a user may buy beyond the public catalogue — for the user card in the panel.
        group.MapGet("/users/{username}/grants", async ([FromRoute] string username, IPlanService plans) =>
        {
            IReadOnlyList<PlanGrantItem>? grants;
            try { grants = await plans.ListGrantsForUserAsync(username); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return grants is null ? Results.NotFound(new ApiError($"User {username} not found.")) : Results.Ok(grants);
        })
        .Produces<IReadOnlyList<PlanGrantItem>>(200)
        .Produces<ApiError>(404)
        .WithSummary("A user's grants to non-public plans, expired ones included (expires_at null = never expires).");

        // Take the right to buy a closed plan away. A subscription already bought on it stays.
        group.MapDelete("/users/{username}/grants/{planCode}", async (
            [FromRoute] string username, [FromRoute] string planCode, IPlanService plans) =>
        {
            bool ok;
            try { ok = await plans.RevokeGrantAsync(username, planCode); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return ok ? Results.NoContent() : Results.NotFound(new ApiError("No such grant."));
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Revoke a user's grant to a non-public plan (an existing subscription on it is untouched).");

        // ── Plans (the tariff catalogue) ────────────────────────────────────────────
        // There is no DELETE on purpose: payments and subscriptions keep a plan_id, and a promo
        // tied to a plan is removed with it (ON DELETE CASCADE). is_active = false takes a plan
        // off sale and keeps the history readable.

        group.MapGet("/plans", async (IPlanService plans) =>
        {
            try { return Results.Ok(await plans.ListPlansAdminAsync()); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<PlanAdminItem>>(200)
        .WithSummary("Every plan, hidden and inactive ones included, with live subscription and grant counts.");

        group.MapPost("/plans", async ([FromBody] PlanUpsertBody? req, IPlanService plans) =>
        {
            if (PlanService.Validate(req, creating: true) is string error)
                return Results.BadRequest(new ApiError(error, "invalid_plan"));

            (PlanWriteStatus status, int id) res;
            try { res = await plans.CreatePlanAsync(req!); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return res.status == PlanWriteStatus.Exists
                ? Results.Json(new ApiError($"A plan with code '{req!.code!.Trim()}' already exists.", "plan_exists"), statusCode: 409)
                : Results.Created($"/admin/plans/{res.id}", new { id = res.id, code = req!.code!.Trim() });
        })
        .Produces(201)
        .Produces<ApiError>(400)
        .Produces<ApiError>(409)
        .WithSummary("Create a plan. Codes are unique case-insensitively.");

        group.MapPut("/plans/{id:int}", async ([FromRoute] int id, [FromBody] PlanUpsertBody? req, IPlanService plans) =>
        {
            if (PlanService.Validate(req, creating: false) is string error)
                return Results.BadRequest(new ApiError(error, "invalid_plan"));

            PlanWriteStatus status;
            try { status = await plans.UpdatePlanAsync(id, req!); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return status switch
            {
                PlanWriteStatus.NotFound => Results.NotFound(new ApiError($"Plan {id} not found.", "plan_not_found")),
                PlanWriteStatus.InUse    => Results.Json(new ApiError(
                    "The plan has pending or live subscriptions: its kind and interval cannot change. Create a new plan instead.",
                    "plan_in_use"), statusCode: 409),
                _ => Results.Ok((await plans.ListPlansAdminAsync(id)).FirstOrDefault()),
            };
        })
        .Produces<PlanAdminItem>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .WithSummary("Replace a plan's fields (code is fixed). Kind/interval are frozen while subscriptions depend on them.");

        group.MapGet("/plans/{id:int}/grants", async ([FromRoute] int id, IPlanService plans) =>
        {
            IReadOnlyList<PlanGrantItem>? grants;
            try { grants = await plans.ListGrantsForPlanAsync(id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return grants is null ? Results.NotFound(new ApiError($"Plan {id} not found.", "plan_not_found")) : Results.Ok(grants);
        })
        .Produces<IReadOnlyList<PlanGrantItem>>(200)
        .Produces<ApiError>(404)
        .WithSummary("Everyone granted a plan, expired grants included.");

        // ── Refunds (support only) ──────────────────────────────────────────────────

        group.MapPost("/payments/{id:int}/refund", async (
            [FromRoute] int id,
            [FromBody]  RefundBody? req,
            HttpContext ctx,
            IBillingService billing) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User admin) return Results.Unauthorized();

            RefundResult res;
            try { res = await billing.RefundAsync(admin.id, id, req ?? new RefundBody(null, null)); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return res.Status switch
            {
                RefundStatusResult.Ok             => Results.Ok(new { status = "refunded", detail = res.Detail }),
                RefundStatusResult.ManualRequired => Results.Ok(new { status = "manual_required", detail = res.Detail }),
                RefundStatusResult.PaymentNotFound=> Results.NotFound(new ApiError("Payment not found.", "payment_not_found")),
                RefundStatusResult.NotRefundable  => Results.BadRequest(new ApiError($"Not refundable ({res.Detail}).", "not_refundable")),
                _                                 => Results.Json(new ApiError("Payment provider error.", "provider_error"), statusCode: 502),
            };
        })
        .Produces(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .Produces<ApiError>(502)
        .WithSummary("Refund a payment (revokes access on success).");

        group.MapGet("/payments", async ([FromQuery] string? user, IPlanService plans) =>
        {
            try { return Results.Ok(await plans.ListPaymentsAsync(user)); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<PaymentAdminItem>>(200)
        .WithSummary("List payments (optionally filtered by ?user=username).");

        // ── Promo codes ─────────────────────────────────────────────────────────────

        group.MapGet("/promocodes", async (IPlanService plans) =>
        {
            try { return Results.Ok(await plans.ListPromosAsync()); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<PromoRow>>(200)
        .WithSummary("List promo codes.");

        group.MapPost("/promocodes", async ([FromBody] PromoUpsertBody req, IPlanService plans, IReferralService referrals) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.code) || req.percent_off is <= 0 or > 100)
                return Results.BadRequest(new ApiError("code and percent_off (1–100) are required."));
            bool ok;
            try
            {
                // Partner codes arrive in the same checkout field: one code, one meaning.
                if (await referrals.IsPartnerCodeAsync(req.code))
                    return Results.Json(new ApiError($"'{req.code.Trim()}' is a partner's referral code.", "code_taken"), statusCode: 409);
                ok = await plans.CreatePromoAsync(req);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return ok ? Results.Created($"/admin/promocodes", new { code = req.code }) : Results.BadRequest(new ApiError("Invalid promo (bad plan_code?)."));
        })
        .Produces(201)
        .Produces<ApiError>(400)
        .WithSummary("Create a percent-off promo code.");

        group.MapDelete("/promocodes/{code}", async ([FromRoute] string code, IPlanService plans) =>
        {
            bool ok;
            try { ok = await plans.DeactivatePromoAsync(code); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return ok ? Results.NoContent() : Results.NotFound(new ApiError("Promo code not found."));
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Deactivate a promo code.");

        MapReferralEndpoints(group);
    }

    /// <summary>
    /// Referral partners: who has a code, what it gives and earns, who it brought, payouts.
    /// Making someone a partner is the admin's call only — there is no self-service sign-up.
    /// </summary>
    private static void MapReferralEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/referrals", async (IReferralService referrals) =>
        {
            try { return Results.Ok(await referrals.ListAsync()); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<ReferralPartnerAdminItem>>(200)
        .WithSummary("Every referral partner with invited/paying counts, revenue, earned, paid out and balance (whole rubles).");

        group.MapGet("/referrals/{username}", async ([FromRoute] string username, IReferralService referrals) =>
        {
            ReferralPartnerDetail? detail;
            try { detail = await referrals.DetailAsync(username); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return detail is null
                ? Results.NotFound(new ApiError($"{username} is not a referral partner.", "not_partner"))
                : Results.Ok(detail);
        })
        .Produces<ReferralPartnerDetail>(200)
        .Produces<ApiError>(404)
        .WithSummary("One partner: their link, the customers they brought, rewards (latest 200) and payouts.");

        // Create or change. Creating needs code + both percents; on a change, absent fields stay
        // as they are — { "is_active": false } is how a partner is switched off (no DELETE: the
        // rewards and payouts are money history).
        group.MapPut("/users/{username}/referral", async (
            [FromRoute] string username, [FromBody] ReferralUpsertBody? req, HttpContext ctx, IReferralService referrals) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User admin) return Results.Unauthorized();

            ReferralWriteStatus status;
            try
            {
                bool exists = await referrals.DetailAsync(username) is not null;
                if (ReferralService.Validate(req, creating: !exists) is string error)
                    return Results.BadRequest(new ApiError(error, "invalid_referral"));
                status = await referrals.UpsertAsync(admin.id, username, req!);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return status switch
            {
                ReferralWriteStatus.UserNotFound => Results.NotFound(new ApiError($"User {username} not found.", "user_not_found")),
                ReferralWriteStatus.CodeTaken    => Results.Json(new ApiError(
                    "This code is already a partner's or a promo code (case does not matter).", "code_taken"), statusCode: 409),
                ReferralWriteStatus.NotPartner   => Results.BadRequest(new ApiError(
                    "To make a partner, send code, discount_percent and reward_percent.", "invalid_referral")),
                _ => Results.Ok((await referrals.DetailAsync(username))!.partner),
            };
        })
        .Produces<ReferralPartnerAdminItem>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .WithSummary("Make a user a referral partner, or change one (code, discount %, reward %, on/off, note).");

        // Record money actually handed to a partner. Refused above the balance.
        group.MapPost("/users/{username}/referral/payouts", async (
            [FromRoute] string username, [FromBody] ReferralPayoutBody? req, HttpContext ctx, IReferralService referrals) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User admin) return Results.Unauthorized();
            if (req?.amount is not (>= 1 and <= 10_000_000))
                return Results.BadRequest(new ApiError("amount must be 1–10000000 (whole rubles).", "invalid_payout"));
            if (req.note is { Length: > 256 })
                return Results.BadRequest(new ApiError("note: up to 256 characters.", "invalid_payout"));

            ReferralWriteStatus status;
            try { status = await referrals.AddPayoutAsync(admin.id, username, req); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return status switch
            {
                ReferralWriteStatus.NotPartner     => Results.NotFound(new ApiError($"{username} is not a referral partner.", "not_partner")),
                ReferralWriteStatus.ExceedsBalance => Results.Json(new ApiError(
                    "The payout is more than the partner's balance.", "payout_exceeds_balance"), statusCode: 409),
                _ => Results.Ok((await referrals.DetailAsync(username))!.partner),
            };
        })
        .Produces<ReferralPartnerAdminItem>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .WithSummary("Record a payout to a partner (whole rubles); refused when it exceeds their balance.");
    }

    /// <summary>
    /// Everything a move needs after its transaction has committed: provision the new node,
    /// de-provision the old one, and drop the user's cached sessions (current_server_id moved).
    ///
    /// A failure here never rolls the binding back — the database is already the truth, and a
    /// node reconciles its user set from it. Provisioning the new node is reported, because the
    /// user cannot connect until it lands; de-provisioning the old one is only logged, because
    /// that node is very likely the unreachable one and is the reason this is being run.
    /// </summary>
    /// <returns>A line for the report when the new node could not be provisioned, else null.</returns>
    private static async Task<string?> AfterMoveAsync(
        int userId, string username, Guid vpnUuid, NodeEndpoint? source, int targetId,
        IAdminServerService svc, INodeNotifier notifier, IMemoryCache cache, ILogger log)
    {
        var uuid = vpnUuid.ToString();
        string? problem = null;

        try
        {
            NodeEndpoint? target = await svc.GetNodeEndpointAsync(targetId);
            // The notifier logs and returns false rather than throwing, so both are failures.
            if (target is null || !await notifier.AddUserAsync(new NodeTarget(target.host, target.auth_password), uuid))
                problem = $"{username}: provision failed on {targetId}";
        }
        catch (Exception ex)
        {
            problem = $"{username}: provision failed on {targetId}: {ex.Message}";
        }

        if (source is not null)
        {
            try
            {
                if (!await notifier.RemoveUserAsync(new NodeTarget(source.host, source.auth_password), uuid))
                    log.LogInformation("Move: could not deprovision {User} from {Server}", username, source.id);
            }
            catch (Exception ex)
            {
                log.LogInformation("Move: could not deprovision {User} from {Server}: {Message}",
                    username, source.id, ex.Message);
            }
        }

        try { SessionCacheOps.EvictSessions(cache, (await svc.GetUserByIdAsync(userId))?.sessions); }
        catch (Exception ex) { log.LogWarning("Move: could not evict sessions of {User}: {Message}", username, ex.Message); }

        return problem;
    }
}
