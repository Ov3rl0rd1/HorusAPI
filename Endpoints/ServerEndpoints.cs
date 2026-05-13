using HorusAPI.Models;
using HorusAPI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace HorusAPI.Endpoints;

public static class ServerEndpoints
{
    public static void MapServerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/servers")
            .WithTags("Servers")
            .RequireAuthorization();

        // ── List all active servers ───────────────────────────────────────────────
        group.MapGet("/", async (IVpnServerService svc) =>
        {
            IEnumerable<ServerListItem> servers;
            try { servers = await svc.GetAvailableServersAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(servers);
        })
        .Produces<IEnumerable<ServerListItem>>(200)
        .WithSummary("List all available VPN servers");

        // ── Best servers (minimal info, sorted by load) ───────────────────────────
        group.MapGet("/best", async (IVpnServerService svc) =>
        {
            IEnumerable<BestServerItem> servers;
            try { servers = await svc.GetBestServersAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(servers);
        })
        .Produces<IEnumerable<BestServerItem>>(200)
        .WithSummary("List best available VPN servers (sorted by load, has capacity)");

        // ── Connect – return rendered Hysteria2 config ────────────────────────────
        group.MapGet("/{id:int}/connect", async (
            [FromRoute] int    id,
            HttpContext        ctx,
            IVpnServerService  svc,
            IConfiguration     cfg) =>
        {
            if (id <= 0)
                return Results.BadRequest(new ApiError("Invalid server id."));

            // Enforce subscription expiry
            string? subExpStr = ctx.User.FindFirst(ApiConsts.SUBSCRIPTION_EXPIRES_AT)?.Value;
            if (subExpStr is not null
                && DateTime.TryParse(subExpStr, out DateTime subExp)
                && subExp <= DateTime.UtcNow)
            {
                return Results.Json(new ApiError("Subscription expired."), statusCode: 403);
            }

            ConnectData? data;
            try { data = await svc.GetConnectDataAsync(id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (data is null)
                return Results.NotFound(new ApiError($"Server {id} not found or unavailable."));

            string? username = ctx.User.FindFirstValue(ClaimTypes.Name)
                ?? ctx.User.FindFirstValue("unique_name");

            if (string.IsNullOrWhiteSpace(username))
                return Results.Unauthorized();

            string? session = ctx.User.FindFirstValue(ApiConsts.SESSION_ID);

            if (string.IsNullOrWhiteSpace(session))
                return Results.Unauthorized();

            var vars = new Dictionary<string, string?>
            {
                [ApiConsts.CONFIG_HOST]          = data.host,
                [ApiConsts.CONFIG_AUTH]          = $"{username}:{session}",
                [ApiConsts.CONFIG_HOP_INTERVAL]  = data.hop,
                [ApiConsts.CONFIG_OBFS_TYPE]     = data.obfs_type,
                [ApiConsts.CONFIG_OBFS_PASSWORD] = data.obfs_password,
                [ApiConsts.CONFIG_SOCKS_PORT]    = cfg["Socks5:Port"]     ?? "1080",
                [ApiConsts.CONFIG_SOCKS_USERNAME]= cfg["Socks5:Username"] ?? "user",
                [ApiConsts.CONFIG_SOCKS_PASSWORD]= cfg["Socks5:Password"] ?? "pass",
            };

            string rendered = ConfigRenderer.Render(ApiConsts.CONFIG_TEMPLATE, vars);
            return Results.Ok(new ConnectResponse(rendered));
        })
        .Produces<ConnectResponse>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(403)
        .Produces<ApiError>(404)
        .WithSummary("Get rendered Hysteria2 config for a specific VPN server");
    }
}