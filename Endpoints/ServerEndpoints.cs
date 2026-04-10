using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;

namespace HorusAPI.Endpoints;

public static class ServerEndpoints
{
    public static void MapServerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/servers")
            .WithTags("Servers")
            .RequireAuthorization();   // all routes here need a valid JWT

        // GET /servers  – list available VPN servers
        group.MapGet("/", async (IVpnServerService svc) =>
        {
            IEnumerable<ServerListItem> servers;
            try { servers = await svc.GetAvailableServersAsync(); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Ok(servers);
        })
        .Produces<IEnumerable<ServerListItem>>(200)
        .WithSummary("List all available VPN servers");

        // GET /servers/{id}/connect  – get connection credentials for a server
        group.MapGet("/{id:int}/connect", async (
            [FromRoute] int id,
            IVpnServerService svc) =>
        {
            if (id <= 0)
                return Results.BadRequest(new ApiError("Invalid server id."));

            ConnectData? data;
            try { data = await svc.GetConnectDataAsync(id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return data is null
                ? Results.NotFound(new ApiError($"Server {id} not found or unavailable."))
                : Results.Ok(data);
        })
        .Produces<ConnectData>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .WithSummary("Get connection data for a specific VPN server");
    }
}
