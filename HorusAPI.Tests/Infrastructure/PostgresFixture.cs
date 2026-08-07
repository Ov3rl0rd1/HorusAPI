using Dapper;
using Npgsql;

namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Spins up a throwaway database on a reachable PostgreSQL server, applies the real
/// <c>init.sql</c> schema, and hands the connection string to the test factory.
///
/// The server is taken from the <c>ConnectionStrings__Postgres</c> env var (what CI
/// sets for its <c>postgres</c> service) or a localhost default. When no server can
/// be reached, <see cref="Available"/> stays false and the integration tests
/// <c>Skip.IfNot</c> instead of failing — so a checkout without Docker still goes green.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public bool Available { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;
    public string SkipReason { get; private set; } = string.Empty;

    private string _adminConnectionString = string.Empty;
    private string _databaseName = string.Empty;

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? Environment.GetEnvironmentVariable("TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres;Gss Encryption Mode=Disable;";

    public async Task InitializeAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(BaseConnectionString);

            // Connect to the maintenance DB to create our isolated one.
            _adminConnectionString = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = "postgres" }.ConnectionString;
            _databaseName = "horus_test_" + Guid.NewGuid().ToString("N")[..12];

            await WaitForServerAsync(_adminConnectionString);

            await using (var admin = new NpgsqlConnection(_adminConnectionString))
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE DATABASE \"{_databaseName}\"");
            }

            builder.Database = _databaseName;
            ConnectionString = builder.ConnectionString;

            var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "init.sql"));
            await using (var conn = new NpgsqlConnection(ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(schema);
            }

            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"PostgreSQL not available for integration tests: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;

        try
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            // Force-disconnect any lingering pooled sessions, then drop.
            NpgsqlConnection.ClearAllPools();
            await admin.ExecuteAsync(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
        catch
        {
            // Best-effort cleanup — a leaked test DB must never fail the run.
        }
    }

    private static async Task WaitForServerAsync(string connectionString)
    {
        // The CI service container may still be starting; retry briefly.
        Exception? last = null;
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await conn.ExecuteAsync("SELECT 1");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw last ?? new InvalidOperationException("PostgreSQL did not become reachable.");
    }
}
