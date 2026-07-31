using HorusAPI.Models;
using HorusAPI.Services;
using System.Net;
using System.Net.Sockets;

namespace HorusAPI.Endpoints;

/// <summary>
/// Egress-IP echo. The client calls this before and after connecting: the address
/// changes to the node's exit IP once the tunnel is up, which is what the IP card
/// in the app displays.
/// </summary>
public static class WhoAmIEndpoints
{
    public static void MapWhoAmIEndpoints(this WebApplication app)
    {
        app.MapGet("/whoami", (HttpContext ctx) =>
        {
            User? user = ctx.Items[ApiConsts.UserHttpContext] as User;
            if (user is null) return Results.Unauthorized();

            // UseForwardedHeaders has already replaced nginx's address with the
            // X-Forwarded-For client, so this is the caller's real egress IP.
            IPAddress? ip = ctx.Connection.RemoteIpAddress;
            if (ip is not null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            // Never let a proxy or the client cache an address that changes the
            // moment the tunnel comes up.
            ctx.Response.Headers.CacheControl = "no-store";

            return Results.Ok(new WhoAmIResponse(
                ip:                    ip?.ToString() ?? "unknown",
                ipVersion:             ip?.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                username:              user.username,
                email:                 user.email,
                emailVerified:         user.email_verified,
                subscriptionExpiresAt: user.expires_at,
                currentServerId:       user.current_server_id,
                observedAt:            DateTime.UtcNow));
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.Session)
        .WithTags("Account")
        .Produces<WhoAmIResponse>(200)
        .Produces(401)
        .WithSummary("Public egress IP as this API sees it, plus the caller's account state");
    }
}
