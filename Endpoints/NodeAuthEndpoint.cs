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
        // Node sync (reconcile + events polling) is trusted, X-API-PASSWORD-authenticated
        // traffic: it gets its own generous per-node budget instead of competing with the
        // user-facing policies. The global per-IP baseline still applies as a backstop.
        var group = app.MapGroup("/node")
            .WithTags("Node")
            .RequireRateLimiting(RateLimitPolicies.Node)
            .AddEndpointFilter(NodeAuthFilter);

        // Node reports what it is actually running: its profile and the client-side offers
        // that go with it. The reply names the profile it should be on.
        group.MapPost("/register", async (
            [FromBody] NodeRegisterRequest req, HttpContext ctx, INodeService svc) =>
        {
            var serverId = (int)ctx.Items[ServerIdItem]!;

            string? assigned;
            try { assigned = await svc.RegisterAsync(serverId, req); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Ok(new NodeRegisterResponse(serverId, assigned));
        })
        .Produces<NodeRegisterResponse>(200)
        .WithSummary("Register what a node is running (profile + client offers); returns the profile it should be on.");

        // Node reports connect/disconnect events + current online count.
        //
        // Answers 200 with a body rather than the old 204: the reply carries this node's
        // profile assignment, so a fleet-wide protocol switch reaches a node that has been up
        // for weeks on its next telemetry post instead of waiting for a restart. Older agents
        // ignore the body, so this stays compatible in both directions.
        group.MapPost("/events", async (
            [FromBody] NodeEventsRequest req, HttpContext ctx, INodeService svc) =>
        {
            var serverId = (int)ctx.Items[ServerIdItem]!;

            string? assigned;
            try { assigned = await svc.ApplyEventsAsync(serverId, req); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Ok(new NodeEventsResponse(assigned));
        })
        .Produces<NodeEventsResponse>(200)
        .WithSummary("Report a node's online users and disconnect events; returns the profile it should be on.");
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
