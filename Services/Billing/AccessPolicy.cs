using HorusAPI.Models;

namespace HorusAPI.Services.Billing;

/// <summary>
/// The single access gate for the paid service. Under the entitlement model a user has
/// access only while they hold a live subscription — <c>users.expires_at</c> is the
/// denormalised cache of that (recomputed by <see cref="IEntitlementService"/> from the
/// <c>subscriptions</c> table). A NULL/past <c>expires_at</c> therefore means "no active
/// subscription", NOT "never expires" — that legacy meaning is gone. Admins bypass the
/// gate so operations never depend on a paid plan.
/// </summary>
public static class AccessPolicy
{
    public static bool HasActiveAccess(User u) =>
        u.is_admin || (u.expires_at.HasValue && u.expires_at.Value > DateTime.UtcNow);

    /// <summary>Advance <paramref name="from"/> by one plan interval (used to set/extend a period).</summary>
    public static DateTime AddInterval(DateTime from, string unit, int count)
    {
        count = Math.Max(1, count);
        return unit switch
        {
            "day"   => from.AddDays(count),
            "week"  => from.AddDays(7 * count),
            "month" => from.AddMonths(count),
            "year"  => from.AddYears(count),
            _       => from.AddMonths(count),
        };
    }
}
