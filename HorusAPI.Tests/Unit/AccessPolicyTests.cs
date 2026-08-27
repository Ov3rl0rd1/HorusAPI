using HorusAPI.Models;
using HorusAPI.Services.Billing;

namespace HorusAPI.Tests.Unit;

public class AccessPolicyTests
{
    private static User User(bool admin, DateTime? expires) => new() { is_admin = admin, expires_at = expires };

    [Fact]
    public void Admin_always_has_access_even_with_no_subscription()
    {
        Assert.True(AccessPolicy.HasActiveAccess(User(admin: true, expires: null)));
        Assert.True(AccessPolicy.HasActiveAccess(User(admin: true, expires: DateTime.UtcNow.AddDays(-10))));
    }

    [Fact]
    public void Non_admin_with_null_expiry_has_no_access()
    {
        // The critical launch fix: NULL no longer means "never expires".
        Assert.False(AccessPolicy.HasActiveAccess(User(admin: false, expires: null)));
    }

    [Fact]
    public void Non_admin_access_follows_the_expiry()
    {
        Assert.True(AccessPolicy.HasActiveAccess(User(admin: false, expires: DateTime.UtcNow.AddDays(1))));
        Assert.False(AccessPolicy.HasActiveAccess(User(admin: false, expires: DateTime.UtcNow.AddSeconds(-1))));
    }

    [Theory]
    [InlineData("day", 3)]
    [InlineData("week", 2)]
    [InlineData("month", 1)]
    [InlineData("year", 1)]
    public void AddInterval_advances_by_the_plan_period(string unit, int count)
    {
        var from = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var to = AccessPolicy.AddInterval(from, unit, count);
        var expected = unit switch
        {
            "day"   => from.AddDays(count),
            "week"  => from.AddDays(7 * count),
            "month" => from.AddMonths(count),
            "year"  => from.AddYears(count),
            _       => from.AddMonths(count)
        };
        Assert.Equal(expected, to);
    }

    [Fact]
    public void AddInterval_treats_a_zero_or_negative_count_as_one()
    {
        var from = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(from.AddMonths(1), AccessPolicy.AddInterval(from, "month", 0));
    }
}
