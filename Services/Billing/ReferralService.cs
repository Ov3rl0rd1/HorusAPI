using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace HorusAPI.Services.Billing;

/// <summary>
/// The referral programme: partners the admin appoints by hand, the customers their code
/// brings, and what those customers' money earns the partner.
///
/// <list type="bullet">
/// <item>A customer is bound to a partner ONCE — by <c>?ref=CODE</c> at registration, or by
/// the code typed into the promo field at checkout — and only while they are not yet a
/// paying customer. First code wins; a partner cannot refer themselves.</item>
/// <item>While the partner is active, the customer gets <c>discount_percent</c> off every
/// purchase, recurring ones included: unlike a first-charge promo, a permanent discount is
/// exactly what Platega's fixed recurring amount can carry.</item>
/// <item>The partner earns <c>reward_percent</c> of every ruble the customer pays, accrued when
/// the money lands (one-time confirm, recurring activation and each renewal) and reversed on a
/// refund or chargeback. Payouts are made by hand and recorded; balance = rewards − payouts.</item>
/// </list>
///
/// Classic Dapper (no [DapperAot]): the money hooks run inside <see cref="BillingService"/>'s
/// transactions. Positional records bind column by column, so SELECT order is record order.
/// </summary>
public interface IReferralService
{
    /// <summary>An active partner by code, case-insensitively; null when unknown or switched off.</summary>
    Task<ReferralPartnerRow?> FindActiveByCodeAsync(string code);

    /// <summary>True when a code belongs to a partner (active or not) — promo codes must not reuse it.</summary>
    Task<bool> IsPartnerCodeAsync(string code);

    /// <summary>The active partner a customer is bound to, or null.</summary>
    Task<ReferralPartnerRow?> PartnerOfAsync(int userId);

    /// <summary>Bind a customer to the partner owning <paramref name="code"/>; see <see cref="ReferralAttach"/>.</summary>
    Task<ReferralAttach> AttachAsync(int userId, string code);

    Task<ReferralMeView> GetMineAsync(int userId);

    /// <summary>
    /// The partner's share of a payment the customer just made, if they have an active partner.
    /// Idempotent on <paramref name="source"/>, so a replayed webhook never pays twice.
    /// </summary>
    Task AccrueAsync(int userId, int? subscriptionId, int? paymentId, int paidAmount, string source);

    /// <summary>Reverse the latest reward of a purchase whose money went back (refund, chargeback).</summary>
    Task ReverseLatestAsync(int subscriptionId);

    // ── Admin ──
    Task<IReadOnlyList<ReferralPartnerAdminItem>> ListAsync();
    Task<ReferralPartnerDetail?> DetailAsync(string username);
    Task<ReferralWriteStatus> UpsertAsync(int adminId, string username, ReferralUpsertBody body);
    Task<ReferralWriteStatus> AddPayoutAsync(int adminId, string username, ReferralPayoutBody body);
}

public class ReferralService(IConfiguration cfg) : IReferralService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    // ── Rules (pure, unit-tested) ────────────────────────────────────────────────

    /// <summary>Codes travel in links and get read out in videos: letters, digits, '-' and '_'.</summary>
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9_-]{3,32}$", RegexOptions.Compiled);

    /// <summary>A bigger discount than this leaves too little to share and too much to abuse.</summary>
    public const int MaxDiscountPercent = 90;

    /// <summary>What is wrong with a partner body, or null.</summary>
    public static string? Validate(ReferralUpsertBody? b, bool creating)
    {
        if (b is null) return "Body is required.";
        if ((creating || b.code is not null) && (b.code is null || !CodePattern.IsMatch(b.code.Trim())))
            return "code: 3–32 characters, letters, digits, '-' or '_'.";
        if (creating && b.discount_percent is null) return "discount_percent is required.";
        if (creating && b.reward_percent is null) return "reward_percent is required.";
        if (b.discount_percent is < 0 or > MaxDiscountPercent)
            return $"discount_percent must be 0–{MaxDiscountPercent}.";
        if (b.reward_percent is < 0 or > 100) return "reward_percent must be 0–100.";
        if (b.note is { Length: > 256 }) return "note: up to 256 characters.";
        return null;
    }

    /// <summary>The partner's share in whole rubles, rounded down — never more than was earned.</summary>
    public static int Share(int paidAmount, int percent) =>
        paidAmount <= 0 || percent <= 0 ? 0 : (int)((long)paidAmount * Math.Min(percent, 100) / 100);

    /// <summary>Where a partner sends people: straight to sign-up, with their code attached.</summary>
    public static string LinkFor(string publicUrl, string code) =>
        $"{publicUrl.TrimEnd('/')}/login?mode=register&ref={Uri.EscapeDataString(code)}";

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

    private const string PartnerCols = "user_id, code, discount_percent, reward_percent, is_active";

    // ── Customer side ────────────────────────────────────────────────────────────

    public async Task<ReferralPartnerRow?> FindActiveByCodeAsync(string code)
    {
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<ReferralPartnerRow>(
            $"SELECT {PartnerCols} FROM referral_partners WHERE lower(code) = lower(@code) AND is_active",
            new { code = code.Trim() });
    }

    public async Task<bool> IsPartnerCodeAsync(string code)
    {
        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM referral_partners WHERE lower(code) = lower(@code))",
            new { code = code.Trim() });
    }

    public async Task<ReferralPartnerRow?> PartnerOfAsync(int userId)
    {
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<ReferralPartnerRow>($"""
            SELECT rp.user_id, rp.code, rp.discount_percent, rp.reward_percent, rp.is_active
            FROM users u JOIN referral_partners rp ON rp.user_id = u.referred_by
            WHERE u.id = @userId AND rp.is_active
            """, new { userId });
    }

    // Someone who has already paid us is not a referral: the partner did not bring them, and a
    // code must not be a way for an existing customer to buy cheaper from now on. Comp grants
    // (provider 'manual') are not payment, so they do not count.
    private const string IsCustomerSql = """
        EXISTS (SELECT 1 FROM payments p
                WHERE p.user_id = u.id AND p.status IN ('confirmed', 'refunded', 'chargebacked'))
        OR EXISTS (SELECT 1 FROM subscriptions s
                   WHERE s.user_id = u.id AND s.provider <> 'manual' AND s.status NOT IN ('pending', 'failed'))
        """;

    public async Task<ReferralAttach> AttachAsync(int userId, string code)
    {
        ReferralPartnerRow? partner = await FindActiveByCodeAsync(code);
        if (partner is null) return ReferralAttach.UnknownCode;
        if (partner.user_id == userId) return ReferralAttach.SelfReferral;

        await using var conn = Connect();

        // One statement, so two checkouts racing with two different codes cannot both win.
        int bound = await conn.ExecuteAsync($"""
            UPDATE users u SET referred_by = @partner, referred_at = NOW()
            WHERE u.id = @userId AND u.referred_by IS NULL AND NOT ({IsCustomerSql})
            """, new { partner = partner.user_id, userId });
        if (bound == 1) return ReferralAttach.Bound;

        var state = await conn.QuerySingleOrDefaultAsync<(int? referred_by, bool customer)>($"""
            SELECT u.referred_by, ({IsCustomerSql}) AS customer FROM users u WHERE u.id = @userId
            """, new { userId });
        if (state.referred_by == partner.user_id) return ReferralAttach.Bound;
        if (state.referred_by is not null) return ReferralAttach.AlreadyReferred;
        return ReferralAttach.ExistingCustomer;
    }

    public async Task<ReferralMeView> GetMineAsync(int userId)
    {
        await using var conn = Connect();

        ReferralPartnerSelfView? self = null;
        var mine = await conn.QuerySingleOrDefaultAsync<ReferralPartnerAdminItem>(
            AdminSelect + " WHERE rp.user_id = @userId", new { userId });
        if (mine is not null)
            self = new ReferralPartnerSelfView(
                mine.code, LinkFor(PublicUrl, mine.code), mine.discount_percent, mine.reward_percent, mine.is_active,
                mine.invited, mine.paying, mine.earned, mine.paid_out, mine.balance);

        var invitedBy = await conn.QuerySingleOrDefaultAsync<ReferralPartnerRow>("""
            SELECT rp.user_id, rp.code, rp.discount_percent, rp.reward_percent, rp.is_active
            FROM users u JOIN referral_partners rp ON rp.user_id = u.referred_by
            WHERE u.id = @userId
            """, new { userId });
        ReferralInvitedView? invited = invitedBy is null ? null
            : new ReferralInvitedView(invitedBy.code, invitedBy.is_active ? invitedBy.discount_percent : 0, invitedBy.is_active);

        return new ReferralMeView(self, invited);
    }

    // ── Money ────────────────────────────────────────────────────────────────────

    public async Task AccrueAsync(int userId, int? subscriptionId, int? paymentId, int paidAmount, string source)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await AccrueInAsync(conn, null, userId, subscriptionId, paymentId, paidAmount, source);
    }

    /// <summary>
    /// <see cref="AccrueAsync"/> on the caller's connection and transaction, so a confirmation and
    /// the reward it earns commit together — or, when anything fails, roll back together and the
    /// provider's retry of the webhook finds a clean slate.
    /// </summary>
    public static async Task AccrueInAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx,
        int userId, int? subscriptionId, int? paymentId, int paidAmount, string source)
    {
        if (paidAmount <= 0) return;

        // The share is taken at the partner's CURRENT percent, and only while they are active:
        // switching a partner off stops what they earn from then on, not what they already had.
        // Integer division rounds down, matching Share().
        await conn.ExecuteAsync("""
            INSERT INTO referral_rewards
                (partner_id, referred_user_id, subscription_id, payment_id, source, paid_amount, percent, amount)
            SELECT rp.user_id, u.id, @subscriptionId, @paymentId, @source, @paidAmount, rp.reward_percent,
                   (@paidAmount::bigint * rp.reward_percent / 100)::int
            FROM users u
            JOIN referral_partners rp ON rp.user_id = u.referred_by
            WHERE u.id = @userId AND rp.is_active AND rp.reward_percent > 0
            ON CONFLICT (source) DO NOTHING
            """, new { userId, subscriptionId, paymentId, paidAmount, source }, tx);
    }

    /// <summary>The idempotency key of one paid period of a subscription (one-time buys use their payment).</summary>
    public static string PeriodSource(int subscriptionId, DateTime periodEnd) =>
        $"subscription:{subscriptionId}:{periodEnd.ToUniversalTime():yyyy-MM-dd}";

    public static string PaymentSource(int paymentId) => $"payment:{paymentId}";

    public async Task ReverseLatestAsync(int subscriptionId)
    {
        await using var conn = Connect();
        await conn.ExecuteAsync("""
            UPDATE referral_rewards SET status = 'reversed', reversed_at = NOW()
            WHERE id = (SELECT id FROM referral_rewards
                        WHERE subscription_id = @subscriptionId AND status = 'accrued'
                        ORDER BY created_at DESC, id DESC LIMIT 1)
            """, new { subscriptionId });
    }

    // ── Admin ────────────────────────────────────────────────────────────────────

    // Column order = ReferralPartnerAdminItem's parameter order. "paying" counts customers with
    // at least one reward that still stands; revenue/earned exclude reversed rewards.
    private const string AdminSelect = """
        SELECT rp.user_id, u.username, u.email, rp.code, rp.discount_percent, rp.reward_percent,
               rp.is_active, rp.note, rp.created_at,
               (SELECT COUNT(*)::int FROM users c WHERE c.referred_by = rp.user_id)                       AS invited,
               (SELECT COUNT(DISTINCT r.referred_user_id)::int FROM referral_rewards r
                 WHERE r.partner_id = rp.user_id AND r.status = 'accrued')                                 AS paying,
               (SELECT COALESCE(SUM(r.paid_amount), 0)::int FROM referral_rewards r
                 WHERE r.partner_id = rp.user_id AND r.status = 'accrued')                                 AS revenue,
               (SELECT COALESCE(SUM(r.amount), 0)::int FROM referral_rewards r
                 WHERE r.partner_id = rp.user_id AND r.status = 'accrued')                                 AS earned,
               (SELECT COALESCE(SUM(p.amount), 0)::int FROM referral_payouts p WHERE p.partner_id = rp.user_id) AS paid_out,
               ((SELECT COALESCE(SUM(r.amount), 0) FROM referral_rewards r
                  WHERE r.partner_id = rp.user_id AND r.status = 'accrued')
                - (SELECT COALESCE(SUM(p.amount), 0) FROM referral_payouts p WHERE p.partner_id = rp.user_id))::int AS balance
        FROM referral_partners rp
        JOIN users u ON u.id = rp.user_id
        """;

    public async Task<IReadOnlyList<ReferralPartnerAdminItem>> ListAsync()
    {
        await using var conn = Connect();
        return (await conn.QueryAsync<ReferralPartnerAdminItem>(
            AdminSelect + " ORDER BY rp.is_active DESC, rp.created_at DESC")).ToList();
    }

    public async Task<ReferralPartnerDetail?> DetailAsync(string username)
    {
        await using var conn = Connect();
        var partner = await conn.QuerySingleOrDefaultAsync<ReferralPartnerAdminItem>(
            AdminSelect + " WHERE u.username = @username", new { username });
        if (partner is null) return null;

        var invited = (await conn.QueryAsync<ReferralInvitedItem>("""
            SELECT c.id AS user_id, c.username, c.created_at, c.referred_at,
                   (SELECT COALESCE(SUM(r.paid_amount), 0)::int FROM referral_rewards r
                     WHERE r.referred_user_id = c.id AND r.partner_id = @p AND r.status = 'accrued') AS paid,
                   (c.expires_at IS NOT NULL AND c.expires_at > NOW()) AS has_access
            FROM users c
            WHERE c.referred_by = @p
            ORDER BY c.referred_at DESC NULLS LAST, c.id DESC
            LIMIT 500
            """, new { p = partner.user_id })).ToList();

        var rewards = (await conn.QueryAsync<ReferralRewardItem>("""
            SELECT r.id, r.referred_user_id, c.username, r.payment_id, r.subscription_id,
                   r.paid_amount, r.percent, r.amount, r.status, r.created_at, r.reversed_at
            FROM referral_rewards r
            LEFT JOIN users c ON c.id = r.referred_user_id
            WHERE r.partner_id = @p
            ORDER BY r.created_at DESC, r.id DESC
            LIMIT 200
            """, new { p = partner.user_id })).ToList();

        var payouts = (await conn.QueryAsync<ReferralPayoutItem>("""
            SELECT p.id, p.amount, p.note, a.username AS created_by, p.created_at
            FROM referral_payouts p
            LEFT JOIN users a ON a.id = p.created_by
            WHERE p.partner_id = @p
            ORDER BY p.created_at DESC, p.id DESC
            """, new { p = partner.user_id })).ToList();

        return new ReferralPartnerDetail(partner, LinkFor(PublicUrl, partner.code), invited, rewards, payouts);
    }

    public async Task<ReferralWriteStatus> UpsertAsync(int adminId, string username, ReferralUpsertBody b)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int? userId = await conn.ExecuteScalarAsync<int?>(
            "SELECT id FROM users WHERE username = @username", new { username }, tx);
        if (userId is null) { await tx.RollbackAsync(); return ReferralWriteStatus.UserNotFound; }

        string? code = b.code?.Trim();
        if (code is not null)
        {
            // One namespace with promo codes: both are typed into the same checkout field, and a
            // code that meant two things would silently pick one of them.
            bool taken = await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM referral_partners WHERE lower(code) = lower(@code) AND user_id <> @userId)
                    OR EXISTS (SELECT 1 FROM promo_codes WHERE lower(code) = lower(@code))
                """, new { code, userId }, tx);
            if (taken) { await tx.RollbackAsync(); return ReferralWriteStatus.CodeTaken; }
        }

        bool exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM referral_partners WHERE user_id = @userId)", new { userId }, tx);

        if (!exists)
        {
            if (code is null || b.discount_percent is null || b.reward_percent is null)
            {
                await tx.RollbackAsync();
                return ReferralWriteStatus.NotPartner;   // the endpoint validated creation already
            }
            await conn.ExecuteAsync("""
                INSERT INTO referral_partners (user_id, code, discount_percent, reward_percent, is_active, note, created_by)
                VALUES (@userId, @code, @discount, @reward, @active, @note, @adminId)
                """, new
            {
                userId, code, discount = (short)b.discount_percent.Value, reward = (short)b.reward_percent.Value,
                active = b.is_active ?? true, note = NullIfBlank(b.note), adminId
            }, tx);
        }
        else
        {
            // Absent fields stay as they are, so "switch off" is { is_active: false } and nothing else.
            await conn.ExecuteAsync("""
                UPDATE referral_partners SET
                    code             = COALESCE(@code, code),
                    discount_percent = COALESCE(@discount, discount_percent),
                    reward_percent   = COALESCE(@reward, reward_percent),
                    is_active        = COALESCE(@active, is_active),
                    note             = CASE WHEN @noteSet THEN @note ELSE note END,
                    updated_at       = NOW()
                WHERE user_id = @userId
                """, new
            {
                userId, code,
                discount = (short?)b.discount_percent, reward = (short?)b.reward_percent,
                active = b.is_active, noteSet = b.note is not null, note = NullIfBlank(b.note)
            }, tx);
        }

        try { await tx.CommitAsync(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ReferralWriteStatus.CodeTaken;   // lost a race for the same code
        }
        return ReferralWriteStatus.Ok;
    }

    public async Task<ReferralWriteStatus> AddPayoutAsync(int adminId, string username, ReferralPayoutBody b)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Lock the partner row so two payouts recorded at once cannot both fit one balance.
        int? partnerId = await conn.ExecuteScalarAsync<int?>("""
            SELECT rp.user_id FROM referral_partners rp JOIN users u ON u.id = rp.user_id
            WHERE u.username = @username FOR UPDATE OF rp
            """, new { username }, tx);
        if (partnerId is null) { await tx.RollbackAsync(); return ReferralWriteStatus.NotPartner; }

        int balance = await conn.ExecuteScalarAsync<int>("""
            SELECT ((SELECT COALESCE(SUM(amount), 0) FROM referral_rewards WHERE partner_id = @p AND status = 'accrued')
                  - (SELECT COALESCE(SUM(amount), 0) FROM referral_payouts WHERE partner_id = @p))::int
            """, new { p = partnerId }, tx);
        if (b.amount!.Value > balance) { await tx.RollbackAsync(); return ReferralWriteStatus.ExceedsBalance; }

        await conn.ExecuteAsync("""
            INSERT INTO referral_payouts (partner_id, amount, note, created_by) VALUES (@p, @amount, @note, @adminId)
            """, new { p = partnerId, amount = b.amount.Value, note = NullIfBlank(b.note), adminId }, tx);

        await tx.CommitAsync();
        return ReferralWriteStatus.Ok;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
