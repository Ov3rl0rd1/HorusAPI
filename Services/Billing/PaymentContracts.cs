namespace HorusAPI.Services.Billing;

/// <summary>
/// Provider-independent payment abstraction. The application core talks only to this
/// interface and the normalised types below; everything Platega-specific lives in
/// <see cref="PlategaProvider"/>. Swapping acquirers = a new adapter + one DI line.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Stable id written to <c>payments.provider</c> / <c>subscriptions.provider</c>.</summary>
    string Name { get; }

    /// <summary>Create a recurring subscription (Platega paymentMethod 6). Returns the provider's
    /// subscription id (our <c>provider_ref</c>) + the redirect the payer must be sent to.</summary>
    Task<ProviderCheckout> CreateSubscriptionAsync(CheckoutRequest req, CancellationToken ct = default);

    /// <summary>Create a one-off payment (Platega v2/transaction/process).</summary>
    Task<ProviderCheckout> CreateOneTimeAsync(CheckoutRequest req, CancellationToken ct = default);

    /// <summary>Fetch a subscription's current state (used to re-verify before granting access).</summary>
    Task<ProviderSubscription?> GetSubscriptionAsync(string providerRef, CancellationToken ct = default);

    /// <summary>Stop future charges. Idempotent.</summary>
    Task CancelSubscriptionAsync(string providerRef, CancellationToken ct = default);

    /// <summary>Whether a charge/transaction can be refunded right now (and at what cost).</summary>
    Task<RefundOutcome> CanRefundAsync(string providerTxnId, CancellationToken ct = default);

    /// <summary>Initiate a refund of a charge/transaction.</summary>
    Task<RefundOutcome> RefundAsync(string providerTxnId, CancellationToken ct = default);

    /// <summary>Authenticate a raw webhook by its headers (constant-time secret compare).</summary>
    bool VerifyWebhook(IHeaderDictionary headers);

    /// <summary>Fold a raw webhook body into a normalised event, or null if it can't be parsed.</summary>
    PaymentEvent? ParseWebhook(string rawBody);
}

/// <summary>What the app asks the provider to charge. Amounts are WHOLE RUBLES.</summary>
public sealed record CheckoutRequest(
    string  PlanCode,
    string  Description,
    int     Amount,          // whole rubles (already net of any promo)
    string  Currency,        // "RUB"
    string  IntervalUnit,    // recurring only: 'day'|'week'|'month'|'year'
    int     IntervalCount,   // recurring only
    int     UserId,          // correlation (metadata.userId / payload)
    string? UserLabel = null,
    string? ReturnUrl = null,
    string? FailedUrl = null);

/// <summary>The provider's answer to a create call: what to persist + where to send the payer.</summary>
public sealed record ProviderCheckout(string ProviderRef, string RedirectUrl, string RawStatus);

/// <summary>A subscription's state as the provider currently sees it.</summary>
public sealed record ProviderSubscription(
    string          Id,
    string          Status,
    int             Amount,
    string          Currency,
    DateTimeOffset? NextChargeAt,
    DateTimeOffset? LastChargeAt);

public enum RefundState { Accepted, ManualRequired, NotSupported, Failed }

/// <param name="Supported">For a cancel-supported probe: whether a refund is possible at all.</param>
public sealed record RefundOutcome(RefundState State, bool Supported, string Message, string Raw);

/// <summary>Normalised webhook event — every Platega callback shape folds into one of these.</summary>
public enum PaymentEventKind
{
    Unknown,
    OneTimeConfirmed, OneTimeCanceled, OneTimeChargebacked,
    SubscriptionActivated, SubscriptionCharged, SubscriptionChargeFailed,
    SubscriptionPastDue, SubscriptionCanceled, SubscriptionFailed, SubscriptionChargebacked
}

/// <param name="ProviderEventId">Idempotency/dedup key synthesised by the adapter.</param>
/// <param name="ProviderRef">Subscription id (recurring) or transaction id (one-time).</param>
/// <param name="ProviderTxnId">The per-charge transaction id, when the event is a charge.</param>
public sealed record PaymentEvent(
    PaymentEventKind Kind,
    string           ProviderEventId,
    string           ProviderRef,
    string?          ProviderTxnId,
    int              Amount,
    string           Currency,
    DateTimeOffset?  NextChargeAt,
    string           Raw);
