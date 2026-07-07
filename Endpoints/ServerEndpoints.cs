using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Services.Auth_Handler;

namespace HorusAPI.Endpoints;

public static class ServerEndpoints
{
    public static void MapServerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/servers")
            .WithTags("Servers")
            .RequireAuthorization();


        group.MapGet("/best", async (IVpnServerService svc) =>
        {
            IEnumerable<BestServerItem> servers;
            try { servers = await svc.GetBestServersAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(servers);
        })
        .Produces<IEnumerable<BestServerItem>>(200)
        .WithSummary("List best available VPN servers (sorted by load, has capacity)");

        group.MapGet("/connect", async (
            HttpContext        ctx,
            IVpnServerService  svc,
            IConfiguration     cfg) =>
        {
            User? user = ctx.Items[ApiConsts.UserHttpContext] as User;

            DateTime? subExp = user?.expires_at;
            if (subExp != null && subExp <= DateTime.UtcNow)
                return Results.Json(new ApiError("Subscription expired."), statusCode: 403);

            ConnectData? data;
            try 
            {
                IEnumerable<BestServerItem> bestServers = await svc.GetBestServersAsync();
                BestServerItem? bestServer = bestServers.FirstOrDefault();

                if (bestServer is null)
                    return Results.NotFound(new ApiError("No available servers."));

                data = await svc.GetConnectDataAsync(bestServer);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (data is null)
                return Results.NotFound(new ApiError($"Server not found or unavailable."));

            string? username = user?.username;

            if (string.IsNullOrWhiteSpace(username))
                return Results.Unauthorized();

            string? session = ctx.User.GetSessionKey();

            if (string.IsNullOrWhiteSpace(session))
                return Results.Unauthorized();

            var vars = new Dictionary<string, string?>
            {
                [ApiConsts.CONFIG_HOST]          = data.host,
                [ApiConsts.CONFIG_AUTH]          = $"{session}",
            };

            return Results.Json(vars);
        })
        .Produces<JsonContent>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(403)
        .Produces<ApiError>(404)
        .WithSummary("Get rendered Hysteria2 config for a specific VPN server");
    }
}