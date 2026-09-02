using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Services.Auth_Handler;
using Microsoft.Extensions.Caching.Memory;

namespace HorusAPI.Endpoints;

/// <summary>
/// The single <b>connection</b> endpoint. The auth source decides audience and format:
/// <list type="bullet">
///   <item><b>X-Session-Key header</b> → the Horus app → JSON: every outbound the node
///   offers, ready to use.</item>
///   <item><b>?key= query</b> → a third-party VPN client using this as a subscription URL →
///   base64 body of the share links the node published.</item>
/// </list>
/// What a client gets is decided entirely by the node's active profile: it reports whole
/// client-side xray outbounds and this endpoint substitutes the user into them. Nothing here
/// knows what a protocol is, so switching one on the fleet needs no change in this file.
/// Node-free on the hot path: provisioning happens when the slot is reserved (purchase /
/// select), so a normal /connect reads one row and does string work.
/// </summary>
public static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this WebApplication app)
    {
        // Under /servers (already proxied to the API by nginx) — distinct from the
        // site's own /connect *page*. Third-party subscription URL:
        //   https://{domain}/servers/connect?key={session}
        app.MapGet("/servers/connect", async (
            HttpContext         ctx,
            IUserService        users,
            IReservationService reservation,
            IVpnServerService   svc,
            INodeNotifier       notifier,
            IMemoryCache        cache) =>
        {
            bool viaHeader = ctx.Request.Headers.TryGetValue(ApiConsts.SESSION_HEADER, out var headerVal)
                             && !string.IsNullOrEmpty(headerVal);
            string? token = viaHeader ? headerVal.ToString() : ctx.Request.Query["key"].FirstOrDefault();

            if (string.IsNullOrEmpty(token)) 
                return Results.Unauthorized();

            User? user;
            try { user = await users.ResolveSessionAsync(token); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            if (user is null) return Results.Unauthorized();

            if (ServerEndpoints.IsExpired(user))
                return Results.Json(new ApiError("Subscription expired.", "subscription_expired"), statusCode: 403);

            // Guarantee a slot. Normally a no-op (reserved at purchase); only the first
            // ever connect for a not-yet-bound account provisions the node here.
            ReserveResult res;
            try { res = await reservation.EnsureReservedAsync(user.id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            if (res.status == ReserveStatus.NoCapacity)
                return Results.Json(new ApiError("No free slots available.", "no_capacity"), statusCode: 409);

            ServerRow? server = await svc.GetConnectDataAsync(res.serverId!.Value);
            if (server is null) return Results.Problem("Server unavailable.", statusCode: 503);

            if (res.newlyReserved)
            {
                await notifier.AddUserAsync(new NodeTarget(server.host, server.auth_password), user.vpn_uuid.ToString());
                SessionCacheOps.EvictSessions(cache, user.sessions);   // current_server_id changed
            }

            // Third-party subscription URL → base64 of the share links the node published.
            // Only offers carrying a `uri` take part: writing one is how a profile opts a
            // protocol into subscriptions, and protocols with no share-link format have none.
            if (!viaHeader)
                return Results.Text(OfferRenderer.Subscription(server, user.vpn_uuid), "text/plain; charset=utf-8");

            // Horus app → the node's own outbounds, with this user substituted in.
            var outbounds = OfferRenderer.RenderOutbounds(server, user.vpn_uuid, OfferRenderer.AudienceApp);

            // A node that has not re-registered since being upgraded, or whose profile failed
            // to render, has nothing to offer. Say so rather than handing back an empty config
            // the client would fail to connect with for no visible reason.
            if (outbounds.Count == 0)
                return Results.Problem("Server has no available configuration.", statusCode: 503);

            var payload = new ConnectResponse(
                new BoundServer(server.server_id, server.name, server.country, server.city, server.host),
                [.. outbounds]);

            return Results.Json(payload);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Connect)
        .WithTags("Connect")
        .Produces<ConnectResponse>(200)
        .Produces(401)
        .Produces<ApiError>(403)
        .Produces<ApiError>(409)
        .Produces(503)
        .WithSummary("Client configuration for the caller's bound node. Header session → JSON outbounds; ?key= → base64 subscription.");
    }
}
