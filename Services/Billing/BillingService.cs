using Dapper;
using HorusAPI.Models;
using HorusAPI.Services.Auth_Handler;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace HorusAPI.Services.Billing;

/// <summary>
/// The payment engine: turns a user's intent into a provider checkout, folds provider
/// webhooks into subscription/period state, and drives cancellation and refunds. It owns
/// the correctness-critical bits — atomic DB writes, idempotent webhook handling, the
/// checkout slot hold — while delegating provider I/O to <see cref="IPaymentProvider"/>,
/// slot accounting to <see cref="IReservationService"/>, and the access cache to
/// <see cref="IEntitlementService"/>. Node (de)provisioning + cache eviction run after commit.
/// </summary>
public interface IBillingService
{
    Task<CheckoutResult> CheckoutAsync(User user, CheckoutBody body);
    Task<SubscriptionView?> GetSubscriptionAsync(int userId);
    Task<CancelStatus> CancelAsync(int userId);
    Task HandleWebhookAsync(PaymentEvent ev);
    Task<RefundResult> RefundAsync(int adminId, int paymentId, RefundBody body);

    /// <summary>Release expired checkout holds and fail the payments behind them. Run periodically.</summary>
    Task<int> SweepAsync();
}

public class BillingService(
    IConfiguration cfg,
    IPaymentProvider provider,
    IPlanService plans,
    IReservationService reservation,
    IEntitlementService entitlement,
    IVpnServerService servers,
    INodeNotifier notifier,
    IMemoryCache cache,
    ILogger<BillingService> log) : IBillingService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    private TimeSpan HoldTtl => TimeSpan.FromMinutes(cfg.GetValue<int?>("Payments:HoldMinutes") ?? 30);

    private string PublicUrl
    {
        get
        {
            string? u = cfg["App:PublicUrl"];
            if (string.IsNullOrWhiteSpace(u))
            {
                string? domain = cfg["DOMAIN"];
                u = string.IsNullOrWhiteSpace(domain) ? "" : $"https://{domain}";
            }
            return u.TrimEnd('/');
        }
    }

    private string ReturnUrl => cfg["Payments:Platega:ReturnUrl"] ?? $"{PublicUrl}/cabinet?paid=1";
    private string FailedUrl => cfg["Payments:Platega:FailedUrl"] ?? $"{PublicUrl}/cabinet?paid=0";

    private const string PaymentCols =
        "id, user_id, plan_id, subscription_id, provider, provider_ref, kind, amount, currency, promo_code_id, discount, status, hold_id";
    private const string SubCols =
        "id, user_id, plan_id, provider, provider_ref, kind, status, current_period_end, cancel_at_period_end, server_id";
    private const string PlanCols =
        "id, code, title, tier, kind, interval_unit, interval_count, amount, currency, is_public, is_active";

    // ── Checkout ──────────────────────────────────────────────────────────────────

    public async Task<CheckoutResult> CheckoutAsync(User user, CheckoutBody body)
    {
        if (string.IsNullOrWhiteSpace(body.plan_code))
            return new CheckoutResult(CheckoutStatus.PlanNotFound);

        PlanRow? plan = await plans.GetPlanForUserAsync(user.id, body.plan_code.Trim());
        if (plan is null) return new CheckoutResult(CheckoutStatus.PlanNotFound);
        if (plan.kind is not ("recurring" or "one_time"))
            return new CheckoutResult(CheckoutStatus.PlanNotFound, Detail: "bad_plan_kind");

        // Price + promo. Amounts are whole rubles; the discount is computed on the server.
        int amount = plan.amount;
        int discount = 0;
        int? promoId = null;

        if (!string.IsNullOrWhiteSpace(body.promo_code))
        {
            // Platega recurring charges a fixed amount every period, so a "first charge only"
            // discount can't be represented on a subscription — promos apply to one-time buys.
            if (plan.kind == "recurring")
                return new CheckoutResult(CheckoutStatus.PromoNotApplicable, Detail: "promo_not_applicable_to_recurring");

            var (promo, reason) = await plans.ValidatePromoAsync(body.promo_code.Trim(), user.id, plan);
            if (promo is null) return new CheckoutResult(CheckoutStatus.PromoInvalid, Detail: reason);

            discount = Pricing.Discount(amount, promo.percent_off);
            promoId = promo.id;
        }

        int finalAmount = amount - discount;

        // Hold a seat before we create the payment, so a sold-out fleet fails the buy.
        HoldResult hold;
        try { hold = await reservation.HoldSlotAsync(user.id, HoldTtl); }
        catch (Exception ex) { log.LogError(ex, "Hold failed during checkout for user {UserId}", user.id); return new CheckoutResult(CheckoutStatus.ProviderError, Detail: "db_error"); }
        if (hold.status == HoldStatus.NoCapacity)
            return new CheckoutResult(CheckoutStatus.NoCapacity);

        // Persist the intent (pending subscription + payment) before talking to the provider.
        int paymentId, subscriptionId;
        try
        {
            await using var conn = Connect();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            subscriptionId = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO subscriptions (user_id, plan_id, provider, kind, status, server_id)
                VALUES (@u, @plan, @prov, @kind, 'pending', @srv)
                RETURNING id
                """, new { u = user.id, plan = plan.id, prov = provider.Name, kind = plan.kind, srv = hold.serverId }, tx);

            paymentId = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO payments (user_id, plan_id, subscription_id, provider, kind, amount, currency, promo_code_id, discount, status, hold_id)
                VALUES (@u, @plan, @sub, @prov, @kind, @amt, @cur, @promo, @disc, 'pending', @hold)
                RETURNING id
                """, new { u = user.id, plan = plan.id, sub = subscriptionId, prov = provider.Name, kind = plan.kind, amt = finalAmount, cur = plan.currency, promo = promoId, disc = discount, hold = hold.holdId }, tx);

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Persisting checkout failed for user {UserId}", user.id);
            await SafeReleaseHold(user.id);
            return new CheckoutResult(CheckoutStatus.ProviderError, Detail: "db_error");
        }

        // Talk to the provider.
        var req = new CheckoutRequest(
            plan.code, DescriptionFor(plan), finalAmount, plan.currency,
            plan.interval_unit, plan.interval_count, user.id, user.username, ReturnUrl, FailedUrl);

        ProviderCheckout checkout;
        try
        {
            checkout = plan.kind == "recurring"
                ? await provider.CreateSubscriptionAsync(req)
                : await provider.CreateOneTimeAsync(req);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Provider checkout failed for user {UserId} (payment {PaymentId})", user.id, paymentId);
            await FailPaymentAsync(paymentId, subscriptionId);
            await SafeReleaseHold(user.id);
            return new CheckoutResult(CheckoutStatus.ProviderError, Detail: "provider_error");
        }

        // Save the provider reference for webhook correlation. For recurring, the reference is
        // the subscription id (also stored on the subscription row); for one-time it's the txn id.
        try
        {
            await using var conn = Connect();
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE payments SET provider_ref = @ref, updated_at = NOW() WHERE id = @id",
                new { @ref = checkout.ProviderRef, id = paymentId });
            if (plan.kind == "recurring")
                await conn.ExecuteAsync("UPDATE subscriptions SET provider_ref = @ref, updated_at = NOW() WHERE id = @id",
                    new { @ref = checkout.ProviderRef, id = subscriptionId });
        }
        catch (Exception ex)
        {
            // The payment is created at the provider; losing our ref here would orphan it.
            // Reconciliation can still recover via provider queries, so log loudly and go on.
            log.LogError(ex, "Failed to persist provider_ref {Ref} for payment {PaymentId}", checkout.ProviderRef, paymentId);
        }

        return new CheckoutResult(CheckoutStatus.Ok,
            new CheckoutView(checkout.RedirectUrl, finalAmount, discount, plan.currency, plan.kind));
    }

    // ── User: read + cancel ─────────────────────────────────────────────────────────

    public async Task<SubscriptionView?> GetSubscriptionAsync(int userId)
    {
        await using var conn = Connect();
        var row = await conn.QuerySingleOrDefaultAsync<SubscriptionRow>($"""
            SELECT {SubCols} FROM subscriptions
            WHERE user_id = @u AND status <> 'pending'
            ORDER BY (current_period_end IS NULL), current_period_end DESC, id DESC
            LIMIT 1
            """, new { u = userId });
        if (row is null) return null;

        string? code = row.plan_id is null ? null
            : await conn.ExecuteScalarAsync<string?>("SELECT code FROM plans WHERE id = @p", new { p = row.plan_id });

        return new SubscriptionView(row.status, code, row.current_period_end, row.cancel_at_period_end, row.kind);
    }

    public async Task<CancelStatus> CancelAsync(int userId)
    {
        SubscriptionRow? sub;
        await using (var conn = Connect())
        {
            sub = await conn.QuerySingleOrDefaultAsync<SubscriptionRow>($"""
                SELECT {SubCols} FROM subscriptions
                WHERE user_id = @u AND kind = 'recurring' AND status IN ('active','past_due')
                      AND provider_ref IS NOT NULL AND cancel_at_period_end = FALSE
                ORDER BY id DESC LIMIT 1
                """, new { u = userId });
        }
        if (sub is null) return CancelStatus.NotFound;

        try { await provider.CancelSubscriptionAsync(sub.provider_ref!); }
        catch (Exception ex) { log.LogError(ex, "Provider cancel failed for subscription {Ref}", sub.provider_ref); return CancelStatus.ProviderError; }

        await using (var conn = Connect())
            await conn.ExecuteAsync("UPDATE subscriptions SET cancel_at_period_end = TRUE, updated_at = NOW() WHERE id = @id", new { id = sub.id });

        // Access stays until current_period_end; the SUBSCRIPTION_CANCELLED webhook finalises status.
        return CancelStatus.Ok;
    }

    // ── Webhooks ─────────────────────────────────────────────────────────────────────

    public async Task HandleWebhookAsync(PaymentEvent ev)
    {
        // Idempotency + audit: record the event. If it already exists AND was processed
        // successfully, skip. If it exists but previously FAILED (or is mid-flight), fall
        // through and reprocess — handlers are idempotent, and Platega retries failures.
        await using (var conn = Connect())
        {
            await conn.OpenAsync();
            int? inserted = await conn.ExecuteScalarAsync<int?>("""
                INSERT INTO webhook_events (provider, provider_event_id, kind, raw)
                VALUES (@prov, @eid, @kind, @raw::jsonb)
                ON CONFLICT (provider, provider_event_id) DO NOTHING
                RETURNING id
                """, new { prov = provider.Name, eid = ev.ProviderEventId, kind = ev.Kind.ToString(), raw = ev.Raw });

            if (inserted is null)
            {
                var prior = await conn.QuerySingleOrDefaultAsync<WebhookRow>(
                    "SELECT processed_at, error FROM webhook_events WHERE provider = @prov AND provider_event_id = @eid",
                    new { prov = provider.Name, eid = ev.ProviderEventId });
                if (prior is { processed_at: not null, error: null })
                {
                    log.LogInformation("Duplicate webhook {EventId} ignored", ev.ProviderEventId);
                    return;
                }
                log.LogInformation("Re-processing previously-unfinished webhook {EventId}", ev.ProviderEventId);
            }
        }

        string? error = null;
        try
        {
            switch (ev.Kind)
            {
                case PaymentEventKind.SubscriptionActivated: await ActivateRecurringAsync(ev); break;
                case PaymentEventKind.SubscriptionCharged:   await ChargeRecurringAsync(ev); break;
                case PaymentEventKind.SubscriptionChargeFailed:
                case PaymentEventKind.SubscriptionPastDue:   await SetSubscriptionStatusAsync(ev.ProviderRef, "past_due"); break;
                case PaymentEventKind.SubscriptionCanceled:  await SetSubscriptionStatusAsync(ev.ProviderRef, "canceled", cancelAtPeriodEnd: true); break;
                case PaymentEventKind.SubscriptionFailed:    await FailRecurringAsync(ev); break;
                case PaymentEventKind.SubscriptionChargebacked: await RevokeRecurringAsync(ev); break;
                case PaymentEventKind.OneTimeConfirmed:      await ConfirmOneTimeAsync(ev); break;
                case PaymentEventKind.OneTimeCanceled:       await FailOneTimeAsync(ev); break;
                case PaymentEventKind.OneTimeChargebacked:   await RevokeOneTimeAsync(ev); break;
                default: log.LogWarning("Unhandled webhook kind {Kind} ({EventId})", ev.Kind, ev.ProviderEventId); break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.LogError(ex, "Webhook processing failed for {EventId}", ev.ProviderEventId);
            throw; // surface so the endpoint returns non-200 and Platega retries
        }
        finally
        {
            await using var conn = Connect();
            await conn.ExecuteAsync("UPDATE webhook_events SET processed_at = NOW(), error = @err WHERE provider = @prov AND provider_event_id = @eid",
                new { err = error, prov = provider.Name, eid = ev.ProviderEventId });
        }
    }

    private async Task ActivateRecurringAsync(PaymentEvent ev)
    {
        SubscriptionRow? sub = await LoadSubByRefAsync(ev.ProviderRef);
        if (sub is null) { log.LogWarning("Activate: no subscription for ref {Ref}", ev.ProviderRef); return; }

        // Re-verify with the provider before granting (defence in depth — webhooks carry no HMAC).
        ProviderSubscription? pv = null;
        try { pv = await provider.GetSubscriptionAsync(ev.ProviderRef); }
        catch (Exception ex) { log.LogWarning(ex, "Re-verify of subscription {Ref} failed; trusting secret-verified webhook", ev.ProviderRef); }

        if (pv is not null && !IsProviderActive(pv.Status))
        {
            log.LogWarning("Provider says subscription {Ref} is '{Status}', not activating", ev.ProviderRef, pv.Status);
            await FailRecurringAsync(ev);
            return;
        }

        PlanRow? plan = await LoadPlanByIdAsync(sub.plan_id);
        DateTime periodEnd = (ev.NextChargeAt ?? pv?.NextChargeAt)?.UtcDateTime
                             ?? PeriodEndFrom(DateTime.UtcNow, plan);

        // Turn the checkout hold into a binding first (its own tx), then flip the subscription.
        ConfirmResult bind = await reservation.ConfirmHoldAsync(sub.user_id);

        await UpdateSubscriptionAndRecomputeAsync(sub.id, sub.user_id, s => s
            .Set("status", "active")
            .Set("current_period_end", periodEnd)
            .Set("server_id", bind.serverId ?? sub.server_id));

        await ProvisionAndEvictAsync(sub.user_id, bind.serverId, bind.newlyBound);
        log.LogInformation("Subscription {Ref} activated for user {UserId} until {End:o}", ev.ProviderRef, sub.user_id, periodEnd);
    }

    private async Task ChargeRecurringAsync(PaymentEvent ev)
    {
        SubscriptionRow? sub = await LoadSubByRefAsync(ev.ProviderRef);
        if (sub is null) { log.LogWarning("Charge: no subscription for ref {Ref}", ev.ProviderRef); return; }

        PlanRow? plan = await LoadPlanByIdAsync(sub.plan_id);
        DateTime from = sub.current_period_end is { } cur && cur > DateTime.UtcNow ? cur : DateTime.UtcNow;
        DateTime periodEnd = ev.NextChargeAt?.UtcDateTime ?? PeriodEndFrom(from, plan);

        await using var conn = Connect();
        await conn.OpenAsync();

        if (!string.IsNullOrEmpty(ev.ProviderTxnId))
            await conn.ExecuteAsync("""
                INSERT INTO subscription_charges (subscription_id, provider_txn_id, amount, status, next_charge_at)
                VALUES (@sub, @txn, @amt, 'confirmed', @next)
                ON CONFLICT (provider_txn_id) DO NOTHING
                """, new { sub = sub.id, txn = ev.ProviderTxnId, amt = ev.Amount, next = ev.NextChargeAt?.UtcDateTime });

        await conn.ExecuteAsync("UPDATE subscriptions SET status = 'active', current_period_end = @end, updated_at = NOW() WHERE id = @id",
            new { end = periodEnd, id = sub.id });
        await EntitlementService.ApplyAsync(conn, null, sub.user_id);
        await EvictUserAsync(conn, sub.user_id);
        log.LogInformation("Subscription {Ref} charged; period now until {End:o}", ev.ProviderRef, periodEnd);
    }

    private async Task FailRecurringAsync(PaymentEvent ev)
    {
        SubscriptionRow? sub = await LoadSubByRefAsync(ev.ProviderRef);
        if (sub is null) return;
        await UpdateSubscriptionAndRecomputeAsync(sub.id, sub.user_id, s => s.Set("status", "failed"));
        await reservation.ReleaseHoldAsync(sub.user_id);   // never bound; give the held seat back
    }

    private async Task RevokeRecurringAsync(PaymentEvent ev)
    {
        SubscriptionRow? sub = await LoadSubByRefAsync(ev.ProviderRef);
        if (sub is null) return;

        if (!string.IsNullOrEmpty(ev.ProviderTxnId))
            await using (var conn = Connect())
                await conn.ExecuteAsync("UPDATE subscription_charges SET status = 'chargebacked' WHERE provider_txn_id = @txn",
                    new { txn = ev.ProviderTxnId });

        await RevokeAndReleaseAsync(sub);
    }

    private async Task ConfirmOneTimeAsync(PaymentEvent ev)
    {
        PaymentRow? pay = await LoadPaymentByRefAsync(ev.ProviderRef, "one_time");
        if (pay is null || pay.subscription_id is null) { log.LogWarning("OneTime confirm: no payment for ref {Ref}", ev.ProviderRef); return; }

        PlanRow? plan = await LoadPlanByIdAsync(pay.plan_id);

        // Stack onto any remaining access.
        DateTime? currentEnd = await CurrentAccessEndAsync(pay.user_id);
        DateTime from = currentEnd is { } c && c > DateTime.UtcNow ? c : DateTime.UtcNow;
        DateTime periodEnd = PeriodEndFrom(from, plan);

        ConfirmResult bind = await reservation.ConfirmHoldAsync(pay.user_id);

        await using (var conn = Connect())
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await conn.ExecuteAsync("""
                UPDATE subscriptions
                SET status = 'active', current_period_end = @end, server_id = COALESCE(@srv, server_id), updated_at = NOW()
                WHERE id = @id
                """, new { end = periodEnd, srv = bind.serverId, id = pay.subscription_id }, tx);
            await conn.ExecuteAsync("UPDATE payments SET status = 'confirmed', updated_at = NOW() WHERE id = @id", new { id = pay.id }, tx);

            // Record the promo redemption now that the money actually landed.
            if (pay.promo_code_id is { } promoId)
            {
                await conn.ExecuteAsync("INSERT INTO promo_redemptions (promo_code_id, user_id, payment_id) VALUES (@p, @u, @pay)",
                    new { p = promoId, u = pay.user_id, pay = pay.id }, tx);
                await conn.ExecuteAsync("UPDATE promo_codes SET redeemed_count = redeemed_count + 1 WHERE id = @p", new { p = promoId }, tx);
            }

            await EntitlementService.ApplyAsync(conn, tx, pay.user_id);
            await tx.CommitAsync();
        }

        await ProvisionAndEvictAsync(pay.user_id, bind.serverId, bind.newlyBound);
        log.LogInformation("One-time payment {Ref} confirmed for user {UserId} until {End:o}", ev.ProviderRef, pay.user_id, periodEnd);
    }

    private async Task FailOneTimeAsync(PaymentEvent ev)
    {
        PaymentRow? pay = await LoadPaymentByRefAsync(ev.ProviderRef, "one_time");
        if (pay is null) return;

        await using (var conn = Connect())
        {
            await conn.ExecuteAsync("UPDATE payments SET status = 'canceled', updated_at = NOW() WHERE id = @id", new { id = pay.id });
            if (pay.subscription_id is { } sid)
                await conn.ExecuteAsync("UPDATE subscriptions SET status = 'failed', updated_at = NOW() WHERE id = @id AND status = 'pending'", new { id = sid });
        }
        await reservation.ReleaseHoldAsync(pay.user_id);
    }

    private async Task RevokeOneTimeAsync(PaymentEvent ev)
    {
        PaymentRow? pay = await LoadPaymentByRefAsync(ev.ProviderRef, "one_time");
        if (pay is null) return;
        await using (var conn = Connect())
            await conn.ExecuteAsync("UPDATE payments SET status = 'chargebacked', updated_at = NOW() WHERE id = @id", new { id = pay.id });

        if (pay.subscription_id is { } sid)
        {
            SubscriptionRow? sub = await LoadSubByIdAsync(sid);
            if (sub is not null) await RevokeAndReleaseAsync(sub);
        }
    }

    // ── Refund (admin) ─────────────────────────────────────────────────────────────

    public async Task<RefundResult> RefundAsync(int adminId, int paymentId, RefundBody body)
    {
        PaymentRow? pay;
        await using (var conn = Connect())
            pay = await conn.QuerySingleOrDefaultAsync<PaymentRow>($"SELECT {PaymentCols} FROM payments WHERE id = @id", new { id = paymentId });
        if (pay is null) return new RefundResult(RefundStatusResult.PaymentNotFound);

        // Which provider transaction to refund? One-time = the payment's own txn; recurring =
        // the latest confirmed charge.
        string? txnId = pay.kind == "one_time"
            ? pay.provider_ref
            : await LatestChargeTxnAsync(pay.subscription_id);
        if (string.IsNullOrEmpty(txnId)) return new RefundResult(RefundStatusResult.NotRefundable, "no_transaction");

        RefundOutcome can;
        try { can = await provider.CanRefundAsync(txnId); }
        catch (Exception ex) { log.LogError(ex, "cancel-supported failed for {Txn}", txnId); return new RefundResult(RefundStatusResult.ProviderError); }
        if (!can.Supported) return new RefundResult(RefundStatusResult.NotRefundable, can.Message);

        RefundOutcome res;
        try { res = await provider.RefundAsync(txnId); }
        catch (Exception ex) { log.LogError(ex, "refund failed for {Txn}", txnId); return new RefundResult(RefundStatusResult.ProviderError); }

        int amount = body.amount ?? pay.amount;
        string status = res.State switch
        {
            RefundState.Accepted       => "accepted",
            RefundState.ManualRequired => "manual_required",
            _                          => "failed"
        };

        await using (var conn = Connect())
            await conn.ExecuteAsync("""
                INSERT INTO refunds (payment_id, admin_id, amount, status, provider_result, reason)
                VALUES (@pay, @admin, @amt, @status, @result, @reason)
                """, new { pay = pay.id, admin = adminId, amt = amount, status, result = res.Raw, reason = body.reason });

        if (res.State == RefundState.ManualRequired)
            return new RefundResult(RefundStatusResult.ManualRequired, res.Message);
        if (res.State != RefundState.Accepted)
            return new RefundResult(RefundStatusResult.ProviderError, res.Message);

        // Accepted → mark refunded and revoke access immediately (per policy).
        await using (var conn = Connect())
            await conn.ExecuteAsync("UPDATE payments SET status = 'refunded', updated_at = NOW() WHERE id = @id", new { id = pay.id });

        if (pay.subscription_id is { } sid)
        {
            SubscriptionRow? sub = await LoadSubByIdAsync(sid);
            if (sub is not null)
            {
                if (sub.kind == "recurring" && !string.IsNullOrEmpty(sub.provider_ref))
                    try { await provider.CancelSubscriptionAsync(sub.provider_ref); } catch (Exception ex) { log.LogWarning(ex, "post-refund cancel failed for {Ref}", sub.provider_ref); }
                await RevokeAndReleaseAsync(sub);
            }
        }

        return new RefundResult(RefundStatusResult.Ok, res.Message);
    }

    // ── Sweeper ─────────────────────────────────────────────────────────────────────

    public async Task<int> SweepAsync()
    {
        int freed = await reservation.SweepExpiredHoldsAsync();

        // Fail the intents whose window has clearly passed (hold TTL + grace). A late webhook
        // still reactivates them by provider_ref, so this only tidies abandoned checkouts.
        var grace = HoldTtl + TimeSpan.FromMinutes(10);
        await using var conn = Connect();
        await conn.OpenAsync();
        int failed = await conn.ExecuteAsync("""
            UPDATE payments SET status = 'failed', updated_at = NOW()
            WHERE status = 'pending' AND created_at < NOW() - @grace
            """, new { grace });
        await conn.ExecuteAsync("""
            UPDATE subscriptions SET status = 'failed', updated_at = NOW()
            WHERE status = 'pending' AND created_at < NOW() - @grace
            """, new { grace });

        if (freed > 0 || failed > 0)
            log.LogInformation("Billing sweep: freed {Freed} hold(s), failed {Failed} stale payment(s)", freed, failed);
        return freed + failed;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────

    /// <summary>Revoke access now (cut the period), free the slot, and de-provision the node.</summary>
    private async Task RevokeAndReleaseAsync(SubscriptionRow sub)
    {
        await UpdateSubscriptionAndRecomputeAsync(sub.id, sub.user_id, s => s
            .Set("status", "canceled")
            .Set("current_period_end", DateTime.UtcNow)
            .Set("cancel_at_period_end", true));

        int? previous = await reservation.ReleaseAsync(sub.user_id);
        await EntitlementRecomputeEvictAsync(sub.user_id);
        if (previous is int oldId) await DeprovisionAsync(sub.user_id, oldId);
    }

    private async Task<DateTime?> CurrentAccessEndAsync(int userId)
    {
        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<DateTime?>("SELECT expires_at FROM users WHERE id = @u", new { u = userId });
    }

    private async Task<SubscriptionRow?> LoadSubByRefAsync(string providerRef)
    {
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<SubscriptionRow>(
            $"SELECT {SubCols} FROM subscriptions WHERE provider_ref = @ref LIMIT 1", new { @ref = providerRef });
    }

    private async Task<SubscriptionRow?> LoadSubByIdAsync(int id)
    {
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<SubscriptionRow>(
            $"SELECT {SubCols} FROM subscriptions WHERE id = @id", new { id });
    }

    private async Task<PaymentRow?> LoadPaymentByRefAsync(string providerRef, string kind)
    {
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<PaymentRow>(
            $"SELECT {PaymentCols} FROM payments WHERE provider_ref = @ref AND kind = @kind ORDER BY id DESC LIMIT 1",
            new { @ref = providerRef, kind });
    }

    private async Task<PlanRow?> LoadPlanByIdAsync(int? planId)
    {
        if (planId is null) return null;
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<PlanRow>($"SELECT {PlanCols} FROM plans WHERE id = @id", new { id = planId });
    }

    private async Task<string?> LatestChargeTxnAsync(int? subscriptionId)
    {
        if (subscriptionId is null) return null;
        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT provider_txn_id FROM subscription_charges WHERE subscription_id = @s AND status = 'confirmed' ORDER BY charged_at DESC LIMIT 1",
            new { s = subscriptionId });
    }

    private async Task SetSubscriptionStatusAsync(string providerRef, string status, bool cancelAtPeriodEnd = false)
    {
        SubscriptionRow? sub = await LoadSubByRefAsync(providerRef);
        if (sub is null) return;
        await UpdateSubscriptionAndRecomputeAsync(sub.id, sub.user_id, s =>
        {
            s.Set("status", status);
            if (cancelAtPeriodEnd) s.Set("cancel_at_period_end", true);
            return s;
        });
    }

    /// <summary>Apply a set of subscription column updates, recompute the access cache, evict — all
    /// in one transaction, then session eviction after commit.</summary>
    private async Task UpdateSubscriptionAndRecomputeAsync(int subId, int userId, Func<SubUpdate, SubUpdate> build)
    {
        var upd = build(new SubUpdate());
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync($"UPDATE subscriptions SET {upd.SetClause}, updated_at = NOW() WHERE id = @__id",
            upd.Parameters(subId), tx);
        await EntitlementService.ApplyAsync(conn, tx, userId);
        await tx.CommitAsync();

        await EvictUserSessionsAsync(userId);
    }

    private async Task EntitlementRecomputeEvictAsync(int userId) => await entitlement.RecomputeAndEvictAsync(userId);

    private async Task EvictUserAsync(NpgsqlConnection conn, int userId)
    {
        string[]? sessions = (await conn.QuerySingleOrDefaultAsync<User>("SELECT * FROM users WHERE id = @u", new { u = userId }))?.sessions;
        SessionCacheOps.EvictSessions(cache, sessions);
    }

    private async Task EvictUserSessionsAsync(int userId)
    {
        await using var conn = Connect();
        await EvictUserAsync(conn, userId);
    }

    /// <summary>Best-effort node provisioning + cache eviction after a binding change.</summary>
    private async Task ProvisionAndEvictAsync(int userId, int? serverId, bool newlyBound)
    {
        await EvictUserSessionsAsync(userId);
        if (!newlyBound || serverId is null) return;

        Guid uuid = await LoadUuidAsync(userId);
        ServerRow? server = await servers.GetConnectDataAsync(serverId.Value);
        if (server is not null)
            await notifier.AddUserAsync(new NodeTarget(server.host, server.auth_password), uuid.ToString());
    }

    private async Task DeprovisionAsync(int userId, int serverId)
    {
        Guid uuid = await LoadUuidAsync(userId);
        ServerRow? server = await servers.GetConnectDataAsync(serverId);
        if (server is not null)
            await notifier.RemoveUserAsync(new NodeTarget(server.host, server.auth_password), uuid.ToString());
    }

    private async Task<Guid> LoadUuidAsync(int userId)
    {
        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<Guid>("SELECT vpn_uuid FROM users WHERE id = @u", new { u = userId });
    }

    private async Task SafeReleaseHold(int userId)
    {
        try { await reservation.ReleaseHoldAsync(userId); } catch (Exception ex) { log.LogError(ex, "Release hold failed for user {UserId}", userId); }
    }

    private async Task FailPaymentAsync(int paymentId, int subscriptionId)
    {
        try
        {
            await using var conn = Connect();
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE payments SET status = 'failed', updated_at = NOW() WHERE id = @id", new { id = paymentId });
            await conn.ExecuteAsync("UPDATE subscriptions SET status = 'failed', updated_at = NOW() WHERE id = @id", new { id = subscriptionId });
        }
        catch (Exception ex) { log.LogError(ex, "Failed to mark payment {PaymentId} failed", paymentId); }
    }

    private static DateTime PeriodEndFrom(DateTime from, PlanRow? plan) =>
        plan is null ? from.AddMonths(1) : AccessPolicy.AddInterval(from, plan.interval_unit, plan.interval_count);

    private static bool IsProviderActive(string? status) =>
        !string.IsNullOrEmpty(status) && status.Contains("activ", StringComparison.OrdinalIgnoreCase);

    private static string DescriptionFor(PlanRow plan) =>
        string.IsNullOrWhiteSpace(plan.title) ? $"Horus VPN — {plan.code}" : $"Horus VPN — {plan.title}";

    private sealed record WebhookRow(DateTime? processed_at, string? error);

    /// <summary>Tiny fluent builder for a whitelisted subscription UPDATE (no user input in identifiers).</summary>
    private sealed class SubUpdate
    {
        private readonly List<string> _sets = [];
        private readonly Dictionary<string, object?> _params = [];

        public SubUpdate Set(string column, object? value)
        {
            string p = "@p_" + column;
            _sets.Add($"{column} = {p}");
            _params[p] = value;
            return this;
        }

        public string SetClause => string.Join(", ", _sets);

        public DynamicParameters Parameters(int id)
        {
            var dp = new DynamicParameters();
            dp.Add("__id", id);
            foreach (var kv in _params) dp.Add(kv.Key, kv.Value);
            return dp;
        }
    }
}
