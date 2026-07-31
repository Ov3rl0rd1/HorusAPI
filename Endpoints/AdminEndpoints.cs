using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;

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

        // Set user subscription
        group.MapPut("/users/{username}/subscription", async (
            [FromRoute] string username,
            [FromBody]  SetSubscriptionRequest req,
            IAdminServerService svc,
            INodeNotifier notifier) =>
        {
            bool updated;
            try { updated = await svc.SetSubscriptionAsync(username, req.expires_at.ToUniversalTime()); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (!updated)
                return Results.NotFound(new ApiError($"User {username} not found."));

            return Results.NoContent();
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Set or extend a user's subscription expiry");

        // Cancel user subscription
        group.MapDelete("/users/{username}/subscription", async (
            [FromRoute] string username,
            IAdminServerService svc,
            INodeNotifier notifier) =>
        {
            bool updated;
            try { updated = await svc.ClearSubscriptionAsync(username); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (!updated)
                return Results.NotFound(new ApiError($"User {username} not found."));
            else
            {
                // TODO
                //notifier.RemoveUserAsync();
            }

            return Results.NoContent();
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .WithSummary("Cancel a user's subscription (set expires_at to null)");
    }
}