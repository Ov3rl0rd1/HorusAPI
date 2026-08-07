using HorusAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Boots the real API in-memory against the throwaway test database, with mail
/// swapped for <see cref="RecordingEmailSender"/> so verification/reset flows can be
/// completed and asserted. Rate limiting, auth and forwarded headers run exactly as
/// in production, so tests isolate themselves per-IP via <c>X-Forwarded-For</c>.
/// </summary>
public sealed class HorusApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public RecordingEmailSender Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Mail:Enabled"]               = "false",
                ["App:PublicUrl"]              = "http://localhost",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
        });
    }
}
