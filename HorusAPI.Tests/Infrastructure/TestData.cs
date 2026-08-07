namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Unique-per-case values so tests never collide in the shared DB or rate-limiter.
/// </summary>
public static class TestData
{
    private static int _ipCounter;
    private static int _userCounter;

    /// <summary>A fresh valid IPv4, so each test occupies its own rate-limit partition.</summary>
    public static string NewIp()
    {
        int n = Interlocked.Increment(ref _ipCounter);
        // 10.b.c.d — plenty of room, always a valid address.
        return $"10.{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }

    public static string NewUsername()
    {
        int n = Interlocked.Increment(ref _userCounter);
        return $"user_{Guid.NewGuid():N}"[..24] + n;
    }

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.com";
}
