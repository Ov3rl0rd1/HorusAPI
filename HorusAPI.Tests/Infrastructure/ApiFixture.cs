namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Collection fixture shared by every integration test: owns the throwaway database
/// and the in-memory API bound to it. One app instance is shared, so the in-memory
/// rate-limiter state persists across tests — tests isolate themselves with a unique
/// <c>X-Forwarded-For</c> IP and unique e-mail per case (see <see cref="TestData"/>).
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public HorusApiFactory? Factory { get; private set; }

    public bool Available => _postgres.Available;
    public string SkipReason => _postgres.SkipReason;
    public string ConnectionString => _postgres.ConnectionString;
    public RecordingEmailSender Email => Factory!.Email;
    public FakePaymentProvider Payments => Factory!.Payments;

    public HttpClient NewClient() => Factory!.CreateClient();

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        if (_postgres.Available)
            Factory = new HorusApiFactory(_postgres.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
