namespace HorusAPI.Services.Billing;

// ── DB row shapes (snake_case → members via MatchNamesWithUnderscores) ─────────

public sealed record PlanRow(
    int id, string code, string title, string tier, string kind,
    string interval_unit, int interval_count, int amount, string currency,
    bool is_public, bool is_active);

public sealed record SubscriptionRow(
    int id, int user_id, int? plan_id, string provider, string? provider_ref,
    string kind, string status, DateTime? current_period_end, bool cancel_at_period_end, int? server_id);

public sealed record PaymentRow(
    int id, int user_id, int? plan_id, int? subscription_id, string provider,
    string? provider_ref, string kind, int amount, string currency,
    int? promo_code_id, int discount, string status, int? hold_id);

public sealed record PromoRow(
    int id, string code, string kind, short percent_off,
    int? max_redemptions, int redeemed_count, int? per_user_limit, int? plan_id,
    DateTime? starts_at, DateTime? ends_at, bool is_active);

// ── User-facing API DTOs ──────────────────────────────────────────────────────

/// <summary>A plan as shown to a user (no internal ids).</summary>
public sealed record PlanView(
    string code, string title, string tier, string kind,
    string interval_unit, int interval_count, int amount, string currency, bool is_public);

public sealed record CheckoutBody(string? plan_code, string? promo_code);

/// <summary>Where to send the payer + what they will be charged (amounts in whole rubles).</summary>
public sealed record CheckoutView(string redirect, int amount, int discount, string currency, string kind);

public sealed record SubscriptionView(
    string status, string? plan_code, DateTime? current_period_end, bool cancel_at_period_end, string kind);

// ── Admin API DTOs ─────────────────────────────────────────────────────────────

/// <summary>A payment as the admin list shows it: the row, plus who paid, for what, and when.</summary>
public sealed record PaymentAdminItem(
    int id, int user_id, string username, int? plan_id, string? plan_code, int? subscription_id,
    string provider, string? provider_ref, string kind, int amount, string currency,
    int? promo_code_id, int discount, string status, int? hold_id, DateTime created_at);

public sealed record GrantBody(string plan_code, DateTime? expires_at);
public sealed record CompBody(DateTime expires_at);
public sealed record RefundBody(int? amount, string? reason);

/// <summary>
/// Create/replace a tariff. On update <c>code</c> is ignored: it is how checkouts, promos and the
/// site name the plan, so it is fixed once created. Amounts are whole rubles.
/// </summary>
public sealed record PlanUpsertBody(
    string? code, string? title, string? tier, string? kind,
    string? interval_unit, int? interval_count, int? amount,
    bool? is_public, bool? is_active);

/// <summary>A tariff as the admin list shows it, with how many people it concerns right now.</summary>
/// <param name="live_subscriptions">Subscriptions on this plan with access at this moment.</param>
/// <param name="grants">Live grants to it (never-expiring, or not expired yet) — only meaningful for a non-public plan.</param>
public sealed record PlanAdminItem(
    int id, string code, string title, string tier, string kind,
    string interval_unit, int interval_count, int amount, string currency,
    bool is_public, bool is_active, DateTime created_at,
    int live_subscriptions, int grants);

/// <summary>One user's access to a non-public plan. <c>expires_at</c> null = бессрочно.</summary>
public sealed record PlanGrantItem(
    int user_id, string username, string? email,
    int plan_id, string plan_code, string plan_title, bool plan_is_public,
    DateTime? expires_at, DateTime created_at, string? granted_by);

public enum PlanWriteStatus { Ok, NotFound, Exists, InUse }

public sealed record PromoUpsertBody(
    string code, short percent_off, int? max_redemptions, int? per_user_limit,
    string? plan_code, DateTime? starts_at, DateTime? ends_at);

// ── Result unions for the service layer ────────────────────────────────────────

public enum CheckoutStatus { Ok, PlanNotFound, PromoInvalid, PromoNotApplicable, NoCapacity, ProviderError }
public sealed record CheckoutResult(CheckoutStatus Status, CheckoutView? View = null, string? Detail = null);

public enum CancelStatus { Ok, NotFound, ProviderError }

public enum RefundStatusResult { Ok, PaymentNotFound, NotRefundable, ManualRequired, ProviderError }
public sealed record RefundResult(RefundStatusResult Status, string? Detail = null);
