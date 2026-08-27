using HorusAPI.Services.Billing;

namespace HorusAPI.Tests.Unit;

/// <summary>The webhook decoder is pure, so every Platega callback shape is asserted here
/// without any HTTP or DB — the one place all the acquirer's field-casing quirks are pinned.</summary>
public class PlategaWebhookTests
{
    // ── One-time (paymentStatus) ──────────────────────────────────────────────────

    [Fact]
    public void OneTime_confirmed()
    {
        var ev = PlategaWebhook.Parse("""{"id":"tx1","amount":500,"currency":"RUB","status":"CONFIRMED","paymentMethod":2}""");
        Assert.NotNull(ev);
        Assert.Equal(PaymentEventKind.OneTimeConfirmed, ev!.Kind);
        Assert.Equal("tx1", ev.ProviderRef);
        Assert.Equal("tx1", ev.ProviderTxnId);
        Assert.Equal("ot:tx1:CONFIRMED", ev.ProviderEventId);
        Assert.Equal(500, ev.Amount);
    }

    [Fact]
    public void OneTime_canceled_and_chargebacked()
    {
        Assert.Equal(PaymentEventKind.OneTimeCanceled,
            PlategaWebhook.Parse("""{"id":"t","amount":1,"currency":"RUB","status":"CANCELED"}""")!.Kind);
        Assert.Equal(PaymentEventKind.OneTimeChargebacked,
            PlategaWebhook.Parse("""{"id":"t","amount":1,"currency":"RUB","status":"CHARGEBACKED"}""")!.Kind);
    }

    // ── Recurring charge (subscriptionTransactionStatus) ──────────────────────────

    [Fact]
    public void Recurring_charge_confirmed_carries_the_charge_txn_and_next_date()
    {
        var ev = PlategaWebhook.Parse("""
            {"Id":"ch1","Amount":100,"Currency":"RUB","Status":"CONFIRMED","PaymentMethod":6,
             "Payload":"","SubscriptionId":"sub1","NextChargeAt":"2026-08-09T09:10:00Z"}
            """);
        Assert.NotNull(ev);
        Assert.Equal(PaymentEventKind.SubscriptionCharged, ev!.Kind);
        Assert.Equal("sub1", ev.ProviderRef);       // correlates to our subscription
        Assert.Equal("ch1", ev.ProviderTxnId);      // unique per charge → idempotency
        Assert.Equal("charge:ch1:CONFIRMED", ev.ProviderEventId);
        Assert.NotNull(ev.NextChargeAt);
    }

    [Fact]
    public void Recurring_charge_canceled_becomes_charge_failed()
    {
        var ev = PlategaWebhook.Parse("""
            {"Id":"ch2","Amount":100,"Currency":"RUB","Status":"CANCELED","SubscriptionId":"sub1","NextChargeAt":null}
            """);
        Assert.Equal(PaymentEventKind.SubscriptionChargeFailed, ev!.Kind);
        Assert.Null(ev.NextChargeAt);
    }

    // ── Subscription status (subscriptionStatus) ──────────────────────────────────

    [Theory]
    [InlineData("SUBSCRIPTION_ACTIVATED", PaymentEventKind.SubscriptionActivated)]
    [InlineData("SUBSCRIPTION_PAST_DUE",  PaymentEventKind.SubscriptionPastDue)]
    [InlineData("SUBSCRIPTION_CANCELLED", PaymentEventKind.SubscriptionCanceled)]
    [InlineData("SUBSCRIPTION_FAILED",    PaymentEventKind.SubscriptionFailed)]
    public void Subscription_status_changes(string status, PaymentEventKind expected)
    {
        var ev = PlategaWebhook.Parse($$"""
            {"Id":"sub1","Amount":100,"Currency":"RUB","Status":"{{status}}","PaymentMethod":6,"SubscriptionId":"sub1"}
            """);
        Assert.NotNull(ev);
        Assert.Equal(expected, ev!.Kind);
        Assert.Equal("sub1", ev.ProviderRef);
        Assert.Null(ev.ProviderTxnId);
        Assert.Equal($"sub:sub1:{status}", ev.ProviderEventId);
    }

    // ── Robustness ────────────────────────────────────────────────────────────────

    [Fact]
    public void Field_casing_is_ignored()
    {
        var ev = PlategaWebhook.Parse("""{"id":"tx3","amount":10,"currency":"rub","status":"confirmed"}""");
        Assert.Equal(PaymentEventKind.OneTimeConfirmed, ev!.Kind);
        Assert.Equal("tx3", ev.ProviderRef);
    }

    [Fact]
    public void Pending_charge_is_unknown_not_an_error()
    {
        var ev = PlategaWebhook.Parse("""{"Id":"ch","Amount":1,"Currency":"RUB","Status":"PENDING","SubscriptionId":"s"}""");
        Assert.Equal(PaymentEventKind.Unknown, ev!.Kind);
    }

    [Fact]
    public void Malformed_or_incomplete_bodies_return_null()
    {
        Assert.Null(PlategaWebhook.Parse("not json"));
        Assert.Null(PlategaWebhook.Parse("""{"amount":1,"currency":"RUB"}"""));     // no status
        Assert.Null(PlategaWebhook.Parse("""{"status":"CONFIRMED"}"""));             // one-time with no id
        Assert.Null(PlategaWebhook.Parse("[]"));                                     // not an object
    }
}
