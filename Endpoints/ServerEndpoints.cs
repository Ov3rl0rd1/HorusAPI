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
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Session);


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
            IConfiguration     cfg,
            INodeNotifier notifier) =>
        {
            User? user = ctx.Items[ApiConsts.UserHttpContext] as User;

            DateTime? subExp = user?.expires_at;
            if (subExp != null && subExp <= DateTime.UtcNow)
                return Results.Json(new ApiError("Subscription expired."), statusCode: 403);

            ServerRow? server;
            try 
            {
                server = await svc.ChooseServerAsync(user!);

                if (server is null)
                    return Results.NotFound(new ApiError("No available servers."));

                await svc.BindUserAsync(user!.id, server.server_id);

                string? email = user?.email;
                
                await notifier.AddUserAsync(new NodeTarget(server.host, server.auth_password), email!, user!.vpn_uuid.ToString());
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            string? username = user?.username;

            if (string.IsNullOrWhiteSpace(username))
                return Results.Unauthorized();

            string? session = ctx.User.GetSessionKey();

            if (string.IsNullOrWhiteSpace(session))
                return Results.Unauthorized();


            string mainVless = ClientConfigBuilder.MainVless(server, user.vpn_uuid);
            string mainHystria = ClientConfigBuilder.MainHysteria(server, user.vpn_uuid);

            var vars = new Dictionary<string, string?>
            {
                [ApiConsts.VLESS_LINK]          = mainVless,
                [ApiConsts.HYSTERIA_LINK]       = mainHystria,
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