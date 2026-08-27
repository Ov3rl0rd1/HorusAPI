using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HorusAPI.Services.Billing;

/// <summary>Raised when Platega returns a non-success status or an unusable body.</summary>
public sealed class PaymentProviderException(string message) : Exception(message);

/// <summary>
/// Platega adapter. All acquirer specifics live here: the <c>X-MerchantId</c>/<c>X-Secret</c>
/// header auth, the interval-code mapping, and the three webhook shapes (paymentStatus,
/// subscriptionTransactionStatus, subscriptionStatus) folded into <see cref="PaymentEvent"/>.
/// Platega sends no HMAC and reuses field casing inconsistently, so webhook parsing is
/// case-insensitive and authenticity leans on the shared secret (constant-time) plus a
/// provider-side re-verification of subscription state before any access is granted.
/// </summary>
public sealed class PlategaProvider(
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ILogger<PlategaProvider> log) : IPaymentProvider
{
    public string Name => "platega";

    private const string MerchantHeader = "X-MerchantId";
    private const string SecretHeader   = "X-Secret";

    private string MerchantId => cfg["Payments:Platega:MerchantId"] ?? "";
    private string Secret     => cfg["Payments:Platega:Secret"] ?? "";

    private static readonly JsonSerializerOptions Json = new(); // member names verbatim (no camelCase)

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<ProviderCheckout> CreateSubscriptionAsync(CheckoutRequest req, CancellationToken ct = default)
    {
        var body = new
        {
            paymentMethod = 6,
            paymentDetails = new
            {
                amount        = req.Amount,
                currency      = req.Currency,
                interval      = IntervalCode(req.IntervalUnit),
                intervalCount = Math.Max(1, req.IntervalCount)
            },
            description = req.Description
        };

        using var doc = await SendAsync(HttpMethod.Post, "transaction/process", body, ct);
        var root = doc.RootElement;

        string? id       = GetString(root, "transactionId");   // = subscriptionId
        string? redirect = GetString(root, "redirect");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(redirect))
            throw new PaymentProviderException("Platega subscription create returned no transactionId/redirect.");

        return new ProviderCheckout(id, redirect, GetString(root, "status") ?? "");
    }

    public async Task<ProviderCheckout> CreateOneTimeAsync(CheckoutRequest req, CancellationToken ct = default)
    {
        var body = new
        {
            paymentDetails = new { amount = req.Amount, currency = req.Currency },
            description    = req.Description,
            @return        = req.ReturnUrl ?? "",
            failedUrl      = req.FailedUrl ?? "",
            payload        = $"user:{req.UserId}",
            metadata       = new { userId = req.UserId.ToString(), userName = req.UserLabel ?? $"user{req.UserId}" }
        };

        using var doc = await SendAsync(HttpMethod.Post, "v2/transaction/process", body, ct);
        var root = doc.RootElement;

        string? id  = GetString(root, "transactionId");
        string? url = GetString(root, "url");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url))
            throw new PaymentProviderException("Platega one-time create returned no transactionId/url.");

        return new ProviderCheckout(id, url, GetString(root, "status") ?? "");
    }

    // ── Query / cancel ──────────────────────────────────────────────────────────

    public async Task<ProviderSubscription?> GetSubscriptionAsync(string providerRef, CancellationToken ct = default)
    {
        JsonDocument doc;
        try { doc = await SendAsync(HttpMethod.Get, $"subscription/{Uri.EscapeDataString(providerRef)}", null, ct); }
        catch (PaymentProviderException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            string? id = GetString(root, "id");
            if (string.IsNullOrEmpty(id)) return null;

            return new ProviderSubscription(
                id,
                GetString(root, "status") ?? "",
                (int)Math.Round(GetNumber(root, "amount")),
                GetString(root, "currencyCode") ?? GetString(root, "currency") ?? "RUB",
                GetDate(root, "nextChargeAt"),
                GetDate(root, "lastChargeAt"));
        }
    }

    public async Task CancelSubscriptionAsync(string providerRef, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"subscription/{Uri.EscapeDataString(providerRef)}/cancel", null, ct);
    }

    // ── Refunds ───────────────────────────────────────────────────────────────

    public async Task<RefundOutcome> CanRefundAsync(string providerTxnId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"transaction/{Uri.EscapeDataString(providerTxnId)}/cancel-supported", null, ct, acceptText: true);
        var root = doc.RootElement;
        bool supported = GetBool(root, "supported");
        string raw = root.GetRawText();
        return new RefundOutcome(
            supported ? RefundState.Accepted : RefundState.NotSupported,
            supported,
            GetString(root, "blockReason") ?? (supported ? "refund available" : "refund not available"),
            raw);
    }

    public async Task<RefundOutcome> RefundAsync(string providerTxnId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"transaction/{Uri.EscapeDataString(providerTxnId)}/cancel", null, ct, acceptText: true);
        var root = doc.RootElement;
        bool accepted = GetBool(root, "accepted");
        bool manual   = GetBool(root, "manualControlRequired");
        string message = GetString(root, "message") ?? "";
        RefundState state = manual ? RefundState.ManualRequired : accepted ? RefundState.Accepted : RefundState.Failed;
        return new RefundOutcome(state, accepted || manual, message, root.GetRawText());
    }

    // ── Webhooks ────────────────────────────────────────────────────────────────

    public bool VerifyWebhook(IHeaderDictionary headers)
    {
        // No configured secret → treat as dev/test and accept (the app still re-verifies
        // subscription state with the provider before granting access).
        if (string.IsNullOrEmpty(Secret) && string.IsNullOrEmpty(MerchantId)) return true;

        string merchant = headers[MerchantHeader].ToString();
        string secret   = headers[SecretHeader].ToString();

        return FixedEquals(merchant, MerchantId) && FixedEquals(secret, Secret);
    }

    public PaymentEvent? ParseWebhook(string rawBody) => PlategaWebhook.Parse(rawBody);

    // ── HTTP plumbing ────────────────────────────────────────────────────────────

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool acceptText = false)
    {
        var http = httpFactory.CreateClient("platega");

        using var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation(MerchantHeader, MerchantId);
        req.Headers.TryAddWithoutValidation(SecretHeader, Secret);
        if (acceptText) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (Exception ex)
        {
            log.LogError(ex, "Platega request {Method} {Path} failed", method, path);
            throw new PaymentProviderException($"Platega request failed: {ex.Message}");
        }

        string content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Platega {Method} {Path} → {Status}: {Body}", method, path, (int)resp.StatusCode, content);
            throw new PaymentProviderException($"Platega returned {(int)resp.StatusCode}.");
        }

        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content); }
        catch (JsonException ex)
        {
            throw new PaymentProviderException($"Platega returned unparseable body: {ex.Message}");
        }
    }

    private static int IntervalCode(string unit) => unit switch
    {
        "day"   => 1,
        "week"  => 2,
        "month" => 3,
        "year"  => 4,
        _       => 3
    };

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ── Case-insensitive JSON readers (Platega mixes Id/id, Amount/amount, …) ────
    internal static string?  GetString(JsonElement o, string name) =>
        TryGet(o, name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    internal static double GetNumber(JsonElement o, string name) =>
        TryGet(o, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    internal static bool GetBool(JsonElement o, string name) =>
        TryGet(o, name, out var v) && (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    internal static DateTimeOffset? GetDate(JsonElement o, string name) =>
        TryGet(o, name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    internal static bool TryGet(JsonElement o, string name, out JsonElement value)
    {
        if (o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
        value = default;
        return false;
    }
}

/// <summary>Pure webhook decoder — no I/O, so it is exhaustively unit-tested.</summary>
public static class PlategaWebhook
{
    public static PaymentEvent? Parse(string rawBody)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawBody); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? status = PlategaProvider.GetString(root, "status")?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(status)) return null;

            string? subId    = PlategaProvider.GetString(root, "subscriptionId");
            string? id       = PlegaId(root);
            int     amount   = (int)Math.Round(PlategaProvider.GetNumber(root, "amount"));
            string  currency = PlategaProvider.GetString(root, "currency")
                             ?? PlategaProvider.GetString(root, "currencyCode") ?? "RUB";
            DateTimeOffset? next = PlategaProvider.GetDate(root, "nextChargeAt");

            // Subscription-related: has a SubscriptionId.
            if (!string.IsNullOrEmpty(subId))
            {
                if (status.StartsWith("SUBSCRIPTION_", StringComparison.Ordinal))
                {
                    var kind = status switch
                    {
                        "SUBSCRIPTION_ACTIVATED"                        => PaymentEventKind.SubscriptionActivated,
                        "SUBSCRIPTION_PAST_DUE"                         => PaymentEventKind.SubscriptionPastDue,
                        "SUBSCRIPTION_CANCELLED" or "SUBSCRIPTION_CANCELED" => PaymentEventKind.SubscriptionCanceled,
                        "SUBSCRIPTION_FAILED"                          => PaymentEventKind.SubscriptionFailed,
                        _                                              => PaymentEventKind.Unknown
                    };
                    return new PaymentEvent(kind, $"sub:{subId}:{status}", subId, null, amount, currency, next, rawBody);
                }

                // Otherwise it's a per-charge callback; Id is the (unique) charge txn id.
                string txn = string.IsNullOrEmpty(id) ? subId : id;
                var chargeKind = status switch
                {
                    "CONFIRMED"                 => PaymentEventKind.SubscriptionCharged,
                    "CANCELED" or "CANCELLED"   => PaymentEventKind.SubscriptionChargeFailed,
                    "CHARGEBACKED"              => PaymentEventKind.SubscriptionChargebacked,
                    _                           => PaymentEventKind.Unknown
                };
                return new PaymentEvent(chargeKind, $"charge:{txn}:{status}", subId, txn, amount, currency, next, rawBody);
            }

            // One-time paymentStatus: providerRef = transaction id.
            if (string.IsNullOrEmpty(id)) return null;
            var oneTimeKind = status switch
            {
                "CONFIRMED"               => PaymentEventKind.OneTimeConfirmed,
                "CANCELED" or "CANCELLED" => PaymentEventKind.OneTimeCanceled,
                "CHARGEBACKED"            => PaymentEventKind.OneTimeChargebacked,
                _                         => PaymentEventKind.Unknown
            };
            return new PaymentEvent(oneTimeKind, $"ot:{id}:{status}", id, id, amount, currency, next, rawBody);
        }
    }

    private static string? PlegaId(JsonElement root) => PlategaProvider.GetString(root, "id");
}
