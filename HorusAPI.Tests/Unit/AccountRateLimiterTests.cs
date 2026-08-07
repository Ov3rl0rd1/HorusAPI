using HorusAPI.Services;

namespace HorusAPI.Tests.Unit;

public class AccountRateLimiterTests
{
    [Fact]
    public async Task Allows_three_per_address_then_blocks()
    {
        var limiter = new AccountRateLimiter();
        string email = "victim@example.com";

        Assert.True(await limiter.TryAcquireAsync(email));
        Assert.True(await limiter.TryAcquireAsync(email));
        Assert.True(await limiter.TryAcquireAsync(email));
        Assert.False(await limiter.TryAcquireAsync(email));   // 4th within the hour
    }

    [Fact]
    public async Task Budget_is_per_address()
    {
        var limiter = new AccountRateLimiter();

        for (int i = 0; i < 3; i++)
            Assert.True(await limiter.TryAcquireAsync("a@example.com"));
        Assert.False(await limiter.TryAcquireAsync("a@example.com"));

        // A different address is unaffected.
        Assert.True(await limiter.TryAcquireAsync("b@example.com"));
    }

    [Fact]
    public async Task Address_matching_is_case_and_whitespace_insensitive()
    {
        var limiter = new AccountRateLimiter();

        Assert.True(await limiter.TryAcquireAsync("Person@Example.com"));
        Assert.True(await limiter.TryAcquireAsync("person@example.com"));
        Assert.True(await limiter.TryAcquireAsync("  PERSON@EXAMPLE.COM  "));
        Assert.False(await limiter.TryAcquireAsync("person@example.com"));   // same bucket, over quota
    }

    [Fact]
    public async Task Blank_address_is_rejected()
    {
        var limiter = new AccountRateLimiter();
        Assert.False(await limiter.TryAcquireAsync(""));
        Assert.False(await limiter.TryAcquireAsync("   "));
    }
}
