using System.Net;
using System.Text.Json;
using Dapper;
using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// A user's monthly traffic is kept here, per user, so a monthly allowance follows them from
/// server to server: nodes report the month-to-date figure, the next node is handed it on
/// provisioning, and the old node's answer to a removal brings back the last bytes.
/// </summary>
public class TrafficTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    private const long GB = 1_000_000_000;
    private static string ThisMonth => DateTime.UtcNow.ToString("yyyy-MM");

    // ── Nodes report, central keeps the user's figure ───────────────────────────

    [SkippableFact]
    public async Task A_nodes_report_is_kept_per_user_and_only_ever_grows()
    {
        RequireDb();
        var client = Client();
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var uuid = await UuidAsync(username);
        var (_, password) = await SeedServerAsync();

        await PostUsageAsync(client, password, new NodeUserUsage(uuid, ThisMonth, 120 * GB, 5 * GB));
        Assert.Equal((120 * GB, 5 * GB), await MonthAsync(username, ThisMonth));

        // A late or repeated report — an old node still holding a user who has since moved —
        // can never lower the month, nor add to it.
        await PostUsageAsync(client, password, new NodeUserUsage(uuid, ThisMonth, 100 * GB, 7 * GB));
        Assert.Equal((120 * GB, 7 * GB), await MonthAsync(username, ThisMonth));

        await PostUsageAsync(client, password, new NodeUserUsage(uuid, ThisMonth, 130 * GB, 7 * GB));
        Assert.Equal((130 * GB, 7 * GB), await MonthAsync(username, ThisMonth));
    }

    [SkippableFact]
    public async Task The_month_does_not_reset_when_the_user_changes_server()
    {
        RequireDb();
        var client = Client();
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var uuid = await UuidAsync(username);
        var (_, passA) = await SeedServerAsync();
        var (_, passB) = await SeedServerAsync();

        // 300 GB on server A; the user moves to B, which was handed the 300 and counts on from it.
        await PostUsageAsync(client, passA, new NodeUserUsage(uuid, ThisMonth, 300 * GB, 0));
        await PostUsageAsync(client, passB, new NodeUserUsage(uuid, ThisMonth, 310 * GB, 0));
        Assert.Equal((310 * GB, 0L), await MonthAsync(username, ThisMonth));

        // And what the next node would be handed is the user's figure, whichever node reported it.
        using var scope = Fixture.Factory!.Services.CreateScope();
        var traffic = scope.ServiceProvider.GetRequiredService<ITrafficService>();
        Assert.Equal(new MonthUsage(ThisMonth, 310 * GB, 0), await traffic.CurrentMonthAsync(Guid.Parse(uuid)));
    }

    [SkippableFact]
    public async Task Months_are_kept_apart_and_bad_lines_are_skipped()
    {
        RequireDb();
        var client = Client();
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var uuid = await UuidAsync(username);
        var (_, password) = await SeedServerAsync();

        await PostUsageAsync(client, password,
            new NodeUserUsage(uuid, "2026-01", 400 * GB, 0),
            new NodeUserUsage(uuid, ThisMonth, 1 * GB, 0),
            new NodeUserUsage(uuid, "2026-13", 9 * GB, 0),                         // not a month
            new NodeUserUsage(uuid, ThisMonth, -5, 0),                             // negative
            new NodeUserUsage(Guid.NewGuid().ToString(), ThisMonth, 9 * GB, 0),    // nobody
            new NodeUserUsage("not-a-uuid", ThisMonth, 9 * GB, 0));

        Assert.Equal((400 * GB, 0L), await MonthAsync(username, "2026-01"));
        Assert.Equal((1 * GB, 0L), await MonthAsync(username, ThisMonth));
    }

    [SkippableFact]
    public async Task Telemetry_without_usage_still_works()
    {
        // Nodes that predate usage reporting send no "usage" at all.
        RequireDb();
        var client = Client();
        var (_, password) = await SeedServerAsync();

        var res = await PostEventsAsync(client, password, new { provisioned_count = 0, online_count = 0, events = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_sees_a_users_months()
    {
        RequireDb();
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var (_, password) = await SeedServerAsync();
        await PostUsageAsync(client, password, new NodeUserUsage(await UuidAsync(username), ThisMonth, 42 * GB, 2 * GB));

        var res = await client.GetWithAsync($"/admin/users/{username}/traffic", TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var month = (await res.ReadJsonAsync())[0];
        Assert.Equal(42 * GB, month.GetProperty("total_bytes").GetInt64());
        Assert.Equal(2 * GB, month.GetProperty("whitelist_bypass_bytes").GetInt64());
    }

    // ── The node channel carries the month across a move ─────────────────────────

    [SkippableFact]
    public async Task Provisioning_hands_the_node_the_users_month_and_removal_brings_back_the_last_bytes()
    {
        RequireDb();
        var client = Client();
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var uuid = await UuidAsync(username);
        var (_, password) = await SeedServerAsync();
        await PostUsageAsync(client, password, new NodeUserUsage(uuid, ThisMonth, 300 * GB, 20 * GB));

        var handler = new CapturingHandler(removeAnswer: new { removed = uuid, usage = new { month = ThisMonth, total_bytes = 307 * GB, whitelist_bypass_bytes = 20 * GB } });
        var notifier = NewNotifier(handler);
        var node = new NodeTarget("node-b.example", "pw");

        // POST /users carries the month the user brings with them.
        Assert.True(await notifier.AddUserAsync(node, uuid));
        var sent = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal(uuid, sent.GetProperty("uuid").GetString());
        Assert.Equal(ThisMonth, sent.GetProperty("usage").GetProperty("month").GetString());
        Assert.Equal(300 * GB, sent.GetProperty("usage").GetProperty("total_bytes").GetInt64());
        Assert.Equal(20 * GB, sent.GetProperty("usage").GetProperty("whitelist_bypass_bytes").GetInt64());

        // DELETE's answer — the month as the user left that node — is kept.
        Assert.True(await notifier.RemoveUserAsync(node, uuid));
        Assert.Equal((307 * GB, 20 * GB), await MonthAsync(username, ThisMonth));
    }

    [SkippableFact]
    public async Task A_new_user_is_provisioned_without_a_month_and_an_old_node_answering_204_is_fine()
    {
        RequireDb();
        var client = Client();
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var uuid = await UuidAsync(username);
        var handler = new CapturingHandler(removeAnswer: null);
        var notifier = NewNotifier(handler);
        var node = new NodeTarget("node-a.example", "pw");

        Assert.True(await notifier.AddUserAsync(node, uuid));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("usage").ValueKind);

        Assert.True(await notifier.RemoveUserAsync(node, uuid));
        Assert.Null(await MonthAsync(username, ThisMonth));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private NodeNotifier NewNotifier(CapturingHandler handler)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = Fixture.ConnectionString,
        }).Build();
        return new NodeNotifier(new SingleClientFactory(handler), cfg,
            new TrafficService(cfg, NullLogger<TrafficService>.Instance), NullLogger<NodeNotifier>.Instance);
    }

    /// <summary>Stands in for a node agent: records what central sends, answers like a node.</summary>
    private sealed class CapturingHandler(object? removeAnswer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return removeAnswer is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(removeAnswer) };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<HttpResponseMessage> PostEventsAsync(HttpClient client, string password, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/node/events") { Content = System.Net.Http.Json.JsonContent.Create(body) };
        req.Headers.Add("X-API-PASSWORD", password);
        req.Headers.Add("X-Forwarded-For", TestData.NewIp());
        return client.SendAsync(req);
    }

    private static async Task PostUsageAsync(HttpClient client, string password, params NodeUserUsage[] usage)
    {
        var res = await PostEventsAsync(client, password, new { provisioned_count = usage.Length, online_count = 0, events = Array.Empty<object>(), usage });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private async Task<(int id, string password)> SeedServerAsync()
    {
        string password = "pw-" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        int id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO vpn_servers (name, country, city, host, auth_password, is_active)
            VALUES ('T', 'TT', 'TC', @host, @password, TRUE) RETURNING id
            """, new { host = "node-" + Guid.NewGuid().ToString("N")[..8] + ".example", password });
        return (id, password);
    }

    private async Task<string> UuidAsync(string username)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return (await conn.ExecuteScalarAsync<Guid>("SELECT vpn_uuid FROM users WHERE username = @username", new { username })).ToString();
    }

    private async Task<(long total, long bypass)?> MonthAsync(string username, string month)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        var row = await conn.QuerySingleOrDefaultAsync<UsageRow>("""
            SELECT t.total_bytes, t.whitelist_bypass_bytes FROM traffic_usage t JOIN users u ON u.id = t.user_id
            WHERE u.username = @username AND t.month = @month::date
            """, new { username, month = month + "-01" });
        return row is null ? null : (row.total_bytes, row.whitelist_bypass_bytes);
    }

    private sealed record UsageRow(long total_bytes, long whitelist_bypass_bytes);
}
