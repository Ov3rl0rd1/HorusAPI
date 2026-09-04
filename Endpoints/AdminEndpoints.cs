using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Services.Billing;

namespace HorusAPI.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting(RateLimitPolicies.Admin);

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
        .Produces<IReadOnlyList<PaymentRow>>(200)
        .WithSummary("List payments (optionally filtered by ?user=username).");

        // ── Promo codes ─────────────────────────────────────────────────────────────

        group.MapGet("/promocodes", async (IPlanService plans) =>
        {
            try { return Results.Ok(await plans.ListPromosAsync()); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<PromoRow>>(200)
        .WithSummary("List promo codes.");

        group.MapPost("/promocodes", async ([FromBody] PromoUpsertBody req, IPlanService plans) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.code) || req.percent_off is <= 0 or > 100)
                return Results.BadRequest(new ApiError("code and percent_off (1–100) are required."));
            bool ok;
            try { ok = await plans.CreatePromoAsync(req); }
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
    }
}