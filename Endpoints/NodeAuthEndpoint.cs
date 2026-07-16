using HorusAPI.Models;
using HorusAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HorusAPI.Endpoints;

/// <summary>
/// Node ⇄ central protocol. Every route authenticates the node by its auth_password
/// carried in the X-API-PASSWORD header (see ApiConsts.API_HEADER). The resolved
/// server id is stashed in HttpContext.Items for the handlers.
/// </summary>
public static class NodeAuthEndpoints
{
    private const string ServerIdItem = "nodeServerId";

    public static void MapNodeAuthEndpoints(this WebApplication app)
    {
        // No "auth" rate limiter here: node sync (reconcile + events polling) is trusted,
        // X-API-PASSWORD-authenticated traffic and must not compete with the login limiter.
        // The global per-IP limiter still applies as a backstop.
        var group = app.MapGroup("/node")
            .WithTags("Node")
            .AddEndpointFilter(NodeAuthFilter);

        // Node reports its public parameters on startup / refresh.
        group.MapPost("/register", async (
            [FromBody] NodeRegisterRequest req, HttpContext ctx, INodeService svc) =>
        {
            var serverId = (int)ctx.Items[ServerIdItem]!;
            try { await svc.RegisterAsync(serverId, req); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(new NodeRegisterResponse(serverId));
        })
        .Produces<NodeRegisterResponse>(200)
        .WithSummary("Register/refresh a node's public connection parameters.");

        // Node reports connect/disconnect events + current online count.
        group.MapPost("/events", async (
            [FromBody] NodeEventsRequest req, HttpContext ctx, INodeService svc) =>
        {
            var serverId = (int)ctx.Items[ServerIdItem]!;
            try { await svc.ApplyEventsAsync(serverId, req); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.NoContent();
        })
        .Produces(204)
        .WithSummary("Report a node's online users and connect/disconnect events.");
    }

    /// <summary>Resolves X-API-PASSWORD → server id, or short-circuits with 401.</summary>
    private static async ValueTask<object?> NodeAuthFilter(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var svc = http.RequestServices.GetRequiredService<INodeService>();

        if (!http.Request.Headers.TryGetValue(ApiConsts.API_HEADER, out var pw))
            return Results.Json(new ApiError($"Missing {ApiConsts.API_HEADER} header."), statusCode: 401);

        int? serverId;
        try { serverId = await svc.ResolveServerIdAsync(pw.ToString()); }
        catch { return Results.Problem("Database error.", statusCode: 503); }

        if (serverId is null)
            return Results.Json(new ApiError("Unknown node credential."), statusCode: 401);

        http.Items[ServerIdItem] = serverId.Value;
        return await next(ctx);
    }
}
