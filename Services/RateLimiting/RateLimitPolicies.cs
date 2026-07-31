using HorusAPI.Models;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace HorusAPI.Services;

/// <summary>
/// Named rate-limiting policies. Attach with <c>.RequireRateLimiting(RateLimitPolicies.X)</c>.
/// The policy name is also what the global chain in <see cref="RateLimitSetup"/> reads back
/// off the endpoint metadata, so tagging an endpoint <see cref="Email"/> is what subscribes
/// it to the per-IP mail layers — there is no second marker to keep in sync.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Anything that puts a message in the outbound mail queue.</summary>
    public const string Email = "email";

    /// <summary>Credential checks (login).</summary>
    public const string Login = "login";

    /// <summary>Guessable-secret checks: e-mail codes and reset tokens.</summary>
    public const string Verify = "verify";

    /// <summary>Ordinary authenticated traffic, partitioned per user.</summary>
    public const string Session = "session";

    /// <summary>Admin routes, partitioned per admin.</summary>
    public const string Admin = "admin";

    /// <summary>Node agent sync, partitioned per node credential.</summary>
    public const string Node = "node";
}

public static class RateLimitSetup
{
    // ── Layer budgets ────────────────────────────────────────────────────────
    // Mail layers (the expensive, abusable ones):
    private const int MailPerIpPerMinute = 3;     // stops a single bot spamming us
    private const int MailPerIpPerHour = 15;      // stops slow-drip attempts that pace themselves
    private const int MailGlobalPerHour = 500;    // protects the outbound queue / our IP reputation
                                                  // (the 4th layer — 3/hour per e-mail address — is
                                                  //  IAccountRateLimiter, applied inside the handlers)

    // Everything else:
    private const int BaselinePerIpPerMinute = 120;
    private const int LoginPerIpPer5Minutes = 15;
    private const int VerifyPerIpPerMinute = 10;
    private const int SessionPerUserPerMinute = 120;
    private const int AdminPerUserPerMinute = 60;
    private const int NodePerNodePerMinute = 300;

    public static void AddHorusRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton<IAccountRateLimiter, AccountRateLimiter>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejected;

            // ── Endpoint policies ────────────────────────────────────────────
            // Mail routes: this policy is the global fallback bucket. The per-IP
            // layers for the same routes live in the global chain below.
            options.AddPolicy<string>(RateLimitPolicies.Email, _ =>
                RateLimitPartition.GetFixedWindowLimiter("mail-global", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = MailGlobalPerHour,
                    Window      = TimeSpan.FromHours(1),
                    QueueLimit  = 0
                }));

            // Login: sliding window so a burst of 15 does not reset on a window edge.
            options.AddPolicy<string>(RateLimitPolicies.Login, ctx =>
                RateLimitPartition.GetSlidingWindowLimiter($"login:{ClientKey(ctx)}", _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit          = LoginPerIpPer5Minutes,
                    Window               = TimeSpan.FromMinutes(5),
                    SegmentsPerWindow    = 5,
                    QueueLimit           = 0
                }));

            // Code / token guessing. Per-secret attempt counters in the DB are the
            // real defence; this just caps how fast an IP can iterate.
            options.AddPolicy<string>(RateLimitPolicies.Verify, ctx =>
                RateLimitPartition.GetFixedWindowLimiter($"verify:{ClientKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = VerifyPerIpPerMinute,
                    Window      = TimeSpan.FromMinutes(1),
                    QueueLimit  = 0
                }));

            options.AddPolicy<string>(RateLimitPolicies.Session, ctx =>
                RateLimitPartition.GetFixedWindowLimiter($"session:{UserKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = SessionPerUserPerMinute,
                    Window      = TimeSpan.FromMinutes(1),
                    QueueLimit  = 0
                }));

            options.AddPolicy<string>(RateLimitPolicies.Admin, ctx =>
                RateLimitPartition.GetFixedWindowLimiter($"admin:{UserKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = AdminPerUserPerMinute,
                    Window      = TimeSpan.FromMinutes(1),
                    QueueLimit  = 0
                }));

            // Node sync is trusted, X-API-PASSWORD-authenticated traffic: keyed per
            // node so one chatty node cannot starve the others.
            options.AddPolicy<string>(RateLimitPolicies.Node, ctx =>
                RateLimitPartition.GetFixedWindowLimiter($"node:{NodeKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = NodePerNodePerMinute,
                    Window      = TimeSpan.FromMinutes(1),
                    QueueLimit  = 0
                }));

            // ── Global chain (runs in addition to the endpoint policy) ───────
            // Every link must grant a permit or the request is rejected. Links that
            // do not apply to the current route return a no-op partition.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(

                // Baseline burst guard for every route, including static 404s.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                    RateLimitPartition.GetFixedWindowLimiter($"base:{ClientKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = BaselinePerIpPerMinute,
                        Window      = TimeSpan.FromMinutes(1),
                        QueueLimit  = 0
                    })),

                // Mail, per-IP short term.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsMailRoute(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter($"mail-min:{ClientKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = MailPerIpPerMinute,
                        Window      = TimeSpan.FromMinutes(1),
                        QueueLimit  = 0
                    })
                    : RateLimitPartition.GetNoLimiter<string>("skip")),

                // Mail, per-IP long term.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsMailRoute(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter($"mail-hour:{ClientKey(ctx)}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = MailPerIpPerHour,
                        Window      = TimeSpan.FromHours(1),
                        QueueLimit  = 0
                    })
                    : RateLimitPartition.GetNoLimiter<string>("skip")));
        });
    }

    private static async ValueTask OnRejected(OnRejectedContext context, CancellationToken ct)
    {
        var response = context.HttpContext.Response;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(NumberFormatInfo.InvariantInfo);

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        await response.WriteAsJsonAsync(
            new ApiError("Too many requests. Please try again later.", "rate_limited"), ct);
    }

    /// <summary>True when the endpoint being hit is tagged with the mail policy.</summary>
    private static bool IsMailRoute(HttpContext ctx) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
            == RateLimitPolicies.Email;

    /// <summary>
    /// Client identity for IP-based partitions. Nginx is the only entry point and
    /// UseForwardedHeaders runs before the limiter, so RemoteIpAddress is the real
    /// client. IPv6 is bucketed by /64 — one subscriber owns the whole prefix and
    /// could otherwise rotate the host part for a fresh budget on every request.
    /// </summary>
    private static string ClientKey(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        return ip.AddressFamily == AddressFamily.InterNetworkV6
            ? Convert.ToHexString(ip.GetAddressBytes(), 0, 8)
            : ip.ToString();
    }

    /// <summary>
    /// Per-user partition key. The limiter runs before authentication, so the user
    /// id is taken from the session token itself ("{id}.{secret}"); unauthenticated
    /// callers fall back to their IP.
    /// </summary>
    private static string UserKey(HttpContext ctx)
    {
        string? session = ctx.Request.Headers[ApiConsts.SESSION_HEADER].FirstOrDefault();
        if (string.IsNullOrEmpty(session)) return $"anon:{ClientKey(ctx)}";

        int dot = session.IndexOf('.');
        return dot > 0 ? session[..dot] : $"anon:{ClientKey(ctx)}";
    }

    private static string NodeKey(HttpContext ctx)
    {
        string? password = ctx.Request.Headers[ApiConsts.API_HEADER].FirstOrDefault();
        return string.IsNullOrEmpty(password)
            ? $"anon:{ClientKey(ctx)}"
            : password.GetHashCode(StringComparison.Ordinal).ToString(NumberFormatInfo.InvariantInfo);
    }
}
