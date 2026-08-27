using System.Collections.Concurrent;
using HorusAPI.Services.Billing;
using Microsoft.AspNetCore.Http;

namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Deterministic <see cref="IPaymentProvider"/> for integration tests — the billing analogue
/// of <see cref="RecordingEmailSender"/>. It never makes a network call: creates hand back a
/// predictable provider ref (which tests read to craft webhooks), webhook parsing reuses the
/// real Platega decoder, and the provider is always "active"/refundable so the app's own
/// state machine is what's under test.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public string Name => "platega";

    public string? LastRef { get; private set; }
    public int Creates { get; private set; }
    public ConcurrentBag<string> Canceled { get; } = [];
    public int Refunds { get; private set; }

    /// <summary>When set, the next create throws — to exercise the checkout provider-error path.</summary>
    public bool FailNextCreate { get; set; }

    public Task<ProviderCheckout> CreateSubscriptionAsync(CheckoutRequest req, CancellationToken ct = default)
        => Create("sub", req);

    public Task<ProviderCheckout> CreateOneTimeAsync(CheckoutRequest req, CancellationToken ct = default)
        => Create("ot", req);

    private Task<ProviderCheckout> Create(string prefix, CheckoutRequest req)
    {
        if (FailNextCreate)
        {
            FailNextCreate = false;
            throw new PaymentProviderException("forced test failure");
        }
        Creates++;
        string reff = $"{prefix}-{Guid.NewGuid():N}";
        LastRef = reff;
        return Task.FromResult(new ProviderCheckout(reff, $"https://pay.example/{reff}", "PENDING"));
    }

    public Task<ProviderSubscription?> GetSubscriptionAsync(string providerRef, CancellationToken ct = default)
        => Task.FromResult<ProviderSubscription?>(
            new ProviderSubscription(providerRef, "Active", 100, "RUB", DateTimeOffset.UtcNow.AddMonths(1), null));

    public Task CancelSubscriptionAsync(string providerRef, CancellationToken ct = default)
    {
        Canceled.Add(providerRef);
        return Task.CompletedTask;
    }

    public Task<RefundOutcome> CanRefundAsync(string providerTxnId, CancellationToken ct = default)
        => Task.FromResult(new RefundOutcome(RefundState.Accepted, true, "ok", "{}"));

    public Task<RefundOutcome> RefundAsync(string providerTxnId, CancellationToken ct = default)
    {
        Refunds++;
        return Task.FromResult(new RefundOutcome(RefundState.Accepted, true, "ok", "{}"));
    }

    public bool VerifyWebhook(IHeaderDictionary headers) => true;

    public PaymentEvent? ParseWebhook(string rawBody) => PlategaWebhook.Parse(rawBody);
}
