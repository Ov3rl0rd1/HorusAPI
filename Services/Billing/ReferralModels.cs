namespace HorusAPI.Services.Billing;

// ── DB rows ──────────────────────────────────────────────────────────────────

/// <summary>A referral partner as stored: whose code it is, what it gives and what it earns.</summary>
public sealed record ReferralPartnerRow(
    int user_id, string code, short discount_percent, short reward_percent, bool is_active);

// ── User-facing ──────────────────────────────────────────────────────────────

/// <summary>
/// GET /billing/referral. <c>partner</c> is set for someone the admin made a partner;
/// <c>invited</c> for a customer who came with a partner's code (what discount they get).
/// Either, both or neither may be null.
/// </summary>
public sealed record ReferralMeView(ReferralPartnerSelfView? partner, ReferralInvitedView? invited);

/// <summary>A partner's own view: their code and link, and what it has brought them (whole rubles).</summary>
public sealed record ReferralPartnerSelfView(
    string code, string link, int discount_percent, int reward_percent, bool is_active,
    int invited, int paying, int earned, int paid_out, int balance);

/// <summary>The discount a referred customer gets now; 0 when their partner is switched off.</summary>
public sealed record ReferralInvitedView(string code, int discount_percent, bool active);

// ── Admin ────────────────────────────────────────────────────────────────────

/// <summary>Make a user a partner, or change one. Percent values are whole percents.</summary>
public sealed record ReferralUpsertBody(
    string? code, int? discount_percent, int? reward_percent, bool? is_active, string? note);

public sealed record ReferralPayoutBody(int? amount, string? note);

/// <summary>
/// A partner as the admin list shows it. Money is whole rubles: <c>revenue</c> is what their
/// customers paid (net of reversals), <c>earned</c> the partner's share of it, <c>balance</c>
/// what is still owed after <c>paid_out</c>.
/// </summary>
public sealed record ReferralPartnerAdminItem(
    int user_id, string username, string? email, string code,
    short discount_percent, short reward_percent, bool is_active, string? note,
    DateTime created_at, int invited, int paying, int revenue, int earned, int paid_out, int balance);

/// <summary>One customer a partner brought, and how much they have paid so far.</summary>
public sealed record ReferralInvitedItem(
    int user_id, string username, DateTime created_at, DateTime? referred_at, int paid, bool has_access);

public sealed record ReferralRewardItem(
    int id, int? referred_user_id, string? username, int? payment_id, int? subscription_id,
    int paid_amount, short percent, int amount, string status, DateTime created_at, DateTime? reversed_at);

public sealed record ReferralPayoutItem(int id, int amount, string? note, string? created_by, DateTime created_at);

public sealed record ReferralPartnerDetail(
    ReferralPartnerAdminItem partner, string link,
    IReadOnlyList<ReferralInvitedItem> invited,
    IReadOnlyList<ReferralRewardItem> rewards,
    IReadOnlyList<ReferralPayoutItem> payouts);

public enum ReferralWriteStatus { Ok, UserNotFound, NotPartner, CodeTaken, ExceedsBalance }

/// <summary>
/// Why a code did not bind a customer to a partner. <c>Bound</c> also covers "already bound to
/// this very partner", so presenting the same code twice is not an error.
/// </summary>
public enum ReferralAttach { Bound, UnknownCode, SelfReferral, AlreadyReferred, ExistingCustomer }
