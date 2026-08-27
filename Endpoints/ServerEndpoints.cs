using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Services.Auth_Handler;
using HorusAPI.Services.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace HorusAPI.Endpoints;

/// <summary>
/// Server <b>selection</b> (distinct from connection): list candidates to TCP-ping, and
/// reserve/move the caller onto one. Both require the app session header. Rendering the
/// actual links lives in <see cref="ConnectEndpoints"/>.
/// </summary>
public static class ServerEndpoints
{
    public static void MapServerEndpoints(this WebApplication app)
    {
        // Candidate nodes for the client to TCP-ping (least-loaded w/ capacity, one per country).
        app.MapGet("/servers", async (IVpnServerService svc) =>
        {
            IEnumerable<PingCandidate> servers;
            try { servers = await svc.GetPingCandidatesAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(servers);
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.Session)
        .WithTags("Servers")
        .Produces<IEnumerable<PingCandidate>>(200)
        .WithSummary("Candidate nodes to TCP-ping: least-loaded with capacity, one per country");

        // Reserve / move the caller onto a node (auto-picks least-loaded when server_id omitted).
        app.MapPost("/servers/select", async (
            [FromBody] SelectServerRequest? req,
            HttpContext         ctx,
            IReservationService reservation,
            IVpnServerService   svc,
            INodeNotifier       notifier,
            IMemoryCache        cache,
            ILogger<Program>    log) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User user)
                return Results.Unauthorized();

            if (IsExpired(user))
                return Results.Json(new ApiError("Subscription expired.", "subscription_expired"), statusCode: 403);

            ReserveResult res;
            try { res = await reservation.SelectAsync(user.id, req?.server_id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (res.status == ReserveStatus.NotFound)
                return Results.NotFound(new ApiError("Server not found or inactive.", "server_not_found"));
            if (res.status == ReserveStatus.NoCapacity)
                return Results.Json(new ApiError("No free slots on the requested server.", "no_capacity"), statusCode: 409);

            await ReprovisionAsync(res, user.vpn_uuid.ToString(), svc, notifier);
            SessionCacheOps.EvictSessions(cache, user.sessions);   // current_server_id changed

            ServerRow? server = await svc.GetConnectDataAsync(res.serverId!.Value);
            if (server is null) return Results.Problem("Server unavailable.", statusCode: 503);

            log.LogInformation("User {UserId} bound to server {ServerId}", user.id, server.server_id);
            return Results.Ok(new BoundServer(server.server_id, server.name, server.country, server.city, server.host));
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.Session)
        .WithTags("Servers")
        .Produces<BoundServer>(200)
        .Produces(401)
        .Produces<ApiError>(403)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .WithSummary("Reserve or move the caller to a node (auto-picks least-loaded when server_id is omitted)");
    }

    /// <summary>Access gate: blocks unless the caller holds a live subscription (or is an admin).
    /// NULL/past <c>expires_at</c> now means "no active subscription" — see <see cref="AccessPolicy"/>.</summary>
    internal static bool IsExpired(User u) => !AccessPolicy.HasActiveAccess(u);

    /// <summary>Best-effort node sync after a binding change: drop the old node, add the new one.</summary>
    internal static async Task ReprovisionAsync(ReserveResult res, string uuid, IVpnServerService svc, INodeNotifier notifier)
    {
        if (res.previousServerId is int oldId)
        {
            ServerRow? old = await svc.GetConnectDataAsync(oldId);
            if (old is not null)
                await notifier.RemoveUserAsync(new NodeTarget(old.host, old.auth_password), uuid);
        }

        if (res.newlyReserved || res.previousServerId is not null)
        {
            ServerRow? srv = await svc.GetConnectDataAsync(res.serverId!.Value);
            if (srv is not null)
                await notifier.AddUserAsync(new NodeTarget(srv.host, srv.auth_password), uuid);
        }
    }
}
