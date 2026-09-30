using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace HorusAPI.Services.Billing;

/// <summary>
/// The tariff catalogue and everything adjacent to it that is not money movement:
/// which plans a user may see/buy (public + granted non-public), promo-code validation,
/// and the admin actions that shape entitlements without a payment (grants, comp
/// subscriptions, promo CRUD, payment history). Money movement lives in <see cref="BillingService"/>.
/// </summary>
public interface IPlanService
{
    /// <summary>Public, active plans only — safe to serve to anonymous callers (landing page).
    /// Never includes non-public ("для своих") plans or any per-user grant.</summary>
    Task<IReadOnlyList<PlanView>> GetPublicPlansAsync();

    Task<IReadOnlyList<PlanView>> GetPlansForUserAsync(int userId);

    /// <summary>The plan a user is allowed to buy under <paramref name="code"/>, or null when it
    /// is missing, inactive, or a non-public plan the user has no grant for.</summary>
    Task<PlanRow?> GetPlanForUserAsync(int userId, string code);

    /// <summary>Validate a promo for (user, plan). Returns the row on success, else a reason code.</summary>
    Task<(PromoRow? promo, string? reason)> ValidatePromoAsync(string code, int userId, PlanRow plan);

    // ── Admin ──
    Task<bool> GrantAsync(int adminId, string username, string planCode, DateTime? expiresAt);
    Task<bool> CompAsync(string username, DateTime until);

    /// <summary>Expire a user's manual/comp subscriptions immediately and recompute access.
    /// Leaves provider (paid) subscriptions alone — those go through refund/cancel.</summary>
    Task<bool> RevokeCompAsync(string username);
    Task<bool> CreatePromoAsync(PromoUpsertBody body);
    Task<IReadOnlyList<PromoRow>> ListPromosAsync();
    Task<bool> DeactivatePromoAsync(string code);
    Task<IReadOnlyList<PaymentAdminItem>> ListPaymentsAsync(string? username);

    // ── Admin: the catalogue itself ──

    /// <summary>Every plan, hidden and switched-off ones included; one plan when <paramref name="id"/> is set.</summary>
    Task<IReadOnlyList<PlanAdminItem>> ListPlansAdminAsync(int? id = null);

    /// <summary>Ok (with the new id) or Exists — codes are compared case-insensitively, like checkout does.</summary>
    Task<(PlanWriteStatus status, int id)> CreatePlanAsync(PlanUpsertBody body);

    /// <summary>
    /// Replace a plan's fields (not its code). InUse when the change would alter the kind or the
    /// billing interval under subscriptions that still depend on it — see the implementation.
    /// </summary>
    Task<PlanWriteStatus> UpdatePlanAsync(int id, PlanUpsertBody body);

    // ── Admin: who may buy a closed plan ──

    /// <summary>A user's grants, expired ones included; null when there is no such user.</summary>
    Task<IReadOnlyList<PlanGrantItem>?> ListGrantsForUserAsync(string username);

    /// <summary>Everyone granted a plan, expired grants included; null when there is no such plan.</summary>
    Task<IReadOnlyList<PlanGrantItem>?> ListGrantsForPlanAsync(int planId);

    /// <summary>Remove a grant. False when the user, the plan or the grant does not exist.</summary>
    Task<bool> RevokeGrantAsync(string username, string planCode);
}

public class PlanService(IConfiguration cfg, IEntitlementService entitlement) : IPlanService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    private const string PlanCols =
        "id, code, title, tier, kind, interval_unit, interval_count, amount, currency, is_public, is_active";

    public async Task<IReadOnlyList<PlanView>> GetPublicPlansAsync()
    {
        // Public catalogue only: no user context, no grants, no non-public tariffs.
        const string sql = $"SELECT {PlanCols} FROM plans WHERE is_active AND is_public ORDER BY amount";
        await using var conn = Connect();
        var rows = await conn.QueryAsync<PlanRow>(sql);
        return rows.Select(ToView).ToList();
    }

    public async Task<IReadOnlyList<PlanView>> GetPlansForUserAsync(int userId)
    {
        const string sql = $"""
            SELECT {PlanCols} FROM plans
            WHERE is_active AND (
                is_public
                OR id IN (SELECT plan_id FROM plan_grants
                          WHERE user_id = @u AND (expires_at IS NULL OR expires_at > NOW()))
            )
            ORDER BY amount
            """;
        await using var conn = Connect();
        var rows = await conn.QueryAsync<PlanRow>(sql, new { u = userId });
        return rows.Select(ToView).ToList();
    }

    private static PlanView ToView(PlanRow p) => new(
        p.code, p.title, p.tier, p.kind, p.interval_unit, p.interval_count, p.amount, p.currency, p.is_public);

    public async Task<PlanRow?> GetPlanForUserAsync(int userId, string code)
    {
        const string sql = $"""
            SELECT {PlanCols} FROM plans
            WHERE is_active AND lower(code) = lower(@code) AND (
                is_public
                OR id IN (SELECT plan_id FROM plan_grants
                          WHERE user_id = @u AND (expires_at IS NULL OR expires_at > NOW()))
            )
            LIMIT 1
            """;
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<PlanRow>(sql, new { u = userId, code });
    }

    public async Task<(PromoRow? promo, string? reason)> ValidatePromoAsync(string code, int userId, PlanRow plan)
    {
        await using var conn = Connect();
        var promo = await conn.QuerySingleOrDefaultAsync<PromoRow>(
            "SELECT id, code, kind, percent_off, max_redemptions, redeemed_count, per_user_limit, plan_id, starts_at, ends_at, is_active FROM promo_codes WHERE lower(code) = lower(@code)",
            new { code });

        if (promo is null || !promo.is_active)                    return (null, "invalid");
        if (promo.starts_at is { } s && s > DateTime.UtcNow)      return (null, "not_started");
        if (promo.ends_at   is { } e && e <= DateTime.UtcNow)     return (null, "expired");
        if (promo.plan_id is { } pid && pid != plan.id)           return (null, "plan_mismatch");
        if (promo.max_redemptions is { } max && promo.redeemed_count >= max) return (null, "exhausted");
        if (promo.percent_off is <= 0 or > 100)                   return (null, "invalid");

        if (promo.per_user_limit is { } lim)
        {
            int used = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM promo_redemptions WHERE promo_code_id = @p AND user_id = @u",
                new { p = promo.id, u = userId });
            if (used >= lim) return (null, "already_used");
        }

        return (promo, null);
    }

    // ── Admin ────────────────────────────────────────────────────────────────────

    public async Task<bool> GrantAsync(int adminId, string username, string planCode, DateTime? expiresAt)
    {
        await using var conn = Connect();
        int? userId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM users WHERE username = @username", new { username });
        if (userId is null) return false;

        int? planId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM plans WHERE lower(code) = lower(@planCode)", new { planCode });
        if (planId is null) return false;

        await conn.ExecuteAsync("""
            INSERT INTO plan_grants (user_id, plan_id, granted_by, expires_at)
            VALUES (@u, @p, @admin, @exp)
            ON CONFLICT (user_id, plan_id) DO UPDATE
            SET granted_by = EXCLUDED.granted_by, expires_at = EXCLUDED.expires_at, created_at = NOW()
            """, new { u = userId, p = planId, admin = adminId, exp = expiresAt?.ToUniversalTime() });
        return true;
    }

    public async Task<bool> CompAsync(string username, DateTime until)
    {
        await using var conn = Connect();
        int? userId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM users WHERE username = @username", new { username });
        if (userId is null) return false;

        // One live comp row per user: extend the existing one, else create it.
        int updated = await conn.ExecuteAsync("""
            UPDATE subscriptions
            SET status = 'comp', current_period_end = @until, updated_at = NOW()
            WHERE user_id = @u AND kind = 'comp' AND status = 'comp'
            """, new { u = userId, until = until.ToUniversalTime() });

        if (updated == 0)
            await conn.ExecuteAsync("""
                INSERT INTO subscriptions (user_id, plan_id, provider, kind, status, current_period_end)
                VALUES (@u, NULL, 'manual', 'comp', 'comp', @until)
                """, new { u = userId, until = until.ToUniversalTime() });

        await entitlement.RecomputeAndEvictAsync(userId.Value);
        return true;
    }

    public async Task<bool> RevokeCompAsync(string username)
    {
        await using var conn = Connect();
        int? userId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM users WHERE username = @username", new { username });
        if (userId is null) return false;

        await conn.ExecuteAsync("""
            UPDATE subscriptions
            SET status = 'canceled', current_period_end = NOW(), updated_at = NOW()
            WHERE user_id = @u AND provider = 'manual' AND status NOT IN ('failed', 'canceled')
            """, new { u = userId });

        await entitlement.RecomputeAndEvictAsync(userId.Value);
        return true;
    }

    public async Task<bool> CreatePromoAsync(PromoUpsertBody body)
    {
        if (string.IsNullOrWhiteSpace(body.code) || body.percent_off is <= 0 or > 100) return false;

        await using var conn = Connect();
        int? planId = null;
        if (!string.IsNullOrWhiteSpace(body.plan_code))
        {
            planId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM plans WHERE lower(code) = lower(@c)", new { c = body.plan_code });
            if (planId is null) return false;
        }

        await conn.ExecuteAsync("""
            INSERT INTO promo_codes (code, kind, percent_off, max_redemptions, per_user_limit, plan_id, starts_at, ends_at, is_active)
            VALUES (@code, 'percent', @pct, @max, @peruser, @plan, @start, @end, TRUE)
            """, new
        {
            code = body.code.Trim(),
            pct = body.percent_off,
            max = body.max_redemptions,
            peruser = body.per_user_limit,
            plan = planId,
            start = body.starts_at?.ToUniversalTime(),
            end = body.ends_at?.ToUniversalTime()
        });
        return true;
    }

    public async Task<IReadOnlyList<PromoRow>> ListPromosAsync()
    {
        await using var conn = Connect();
        var rows = await conn.QueryAsync<PromoRow>(
            "SELECT id, code, kind, percent_off, max_redemptions, redeemed_count, per_user_limit, plan_id, starts_at, ends_at, is_active FROM promo_codes ORDER BY id DESC");
        return rows.ToList();
    }

    public async Task<bool> DeactivatePromoAsync(string code)
    {
        await using var conn = Connect();
        return await conn.ExecuteAsync("UPDATE promo_codes SET is_active = FALSE WHERE lower(code) = lower(@code)", new { code }) > 0;
    }

    public async Task<IReadOnlyList<PaymentAdminItem>> ListPaymentsAsync(string? username)
    {
        // Column order is the record's parameter order — classic Dapper binds a positional
        // record through its constructor, and matches it column by column.
        const string sql = """
            SELECT p.id, p.user_id, u.username, p.plan_id, pl.code AS plan_code, p.subscription_id,
                   p.provider, p.provider_ref, p.kind, p.amount, p.currency, p.promo_code_id,
                   p.discount, p.status, p.hold_id, p.created_at
            FROM payments p
            JOIN users u       ON u.id  = p.user_id
            LEFT JOIN plans pl ON pl.id = p.plan_id
            WHERE @username::text IS NULL OR u.username = @username
            ORDER BY p.id DESC
            LIMIT 500
            """;

        var filter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();

        await using var conn = Connect();
        return (await conn.QueryAsync<PaymentAdminItem>(sql, new { username = filter })).ToList();
    }

    // ── Admin: the catalogue ────────────────────────────────────────────────────

    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex TierPattern = new(@"^[a-z0-9_-]{1,16}$", RegexOptions.Compiled);

    public static readonly string[] Kinds = ["recurring", "one_time"];
    public static readonly string[] Units = ["day", "week", "month", "year"];

    /// <summary>
    /// What is wrong with a plan body, or null. Pure, so the rules are unit-tested rather than
    /// discovered through a 500 from a CHECK the schema does not have.
    /// </summary>
    public static string? Validate(PlanUpsertBody? b, bool creating)
    {
        if (b is null) return "Body is required.";
        if (creating && (b.code is null || !CodePattern.IsMatch(b.code.Trim())))
            return "code: 1–64 characters, letters, digits, '-', '_' or '.'.";
        if (string.IsNullOrWhiteSpace(b.title) || b.title.Trim().Length > 128)
            return "title is required (up to 128 characters).";
        if (!string.IsNullOrWhiteSpace(b.tier) && !TierPattern.IsMatch(b.tier.Trim()))
            return "tier: up to 16 lowercase letters, digits, '-' or '_'.";
        if (b.kind is null || !Kinds.Contains(b.kind))
            return "kind must be 'recurring' or 'one_time'.";
        if (b.interval_unit is null || !Units.Contains(b.interval_unit))
            return "interval_unit must be day, week, month or year.";
        if (b.interval_count is not (>= 1 and <= 365))
            return "interval_count must be 1–365.";
        if (b.amount is not (>= 1 and <= 1_000_000))
            return "amount must be 1–1000000 (whole rubles).";
        return null;
    }

    private static string TierOf(PlanUpsertBody b) =>
        string.IsNullOrWhiteSpace(b.tier) ? "standard" : b.tier.Trim();

    public async Task<IReadOnlyList<PlanAdminItem>> ListPlansAdminAsync(int? id = null)
    {
        // Column order is the record's parameter order (classic Dapper binds positional records
        // through the constructor). Access is counted the way EntitlementService grants it.
        const string sql = """
            SELECT p.id, p.code, p.title, p.tier, p.kind, p.interval_unit, p.interval_count,
                   p.amount, p.currency, p.is_public, p.is_active, p.created_at,
                   (SELECT COUNT(*)::int FROM subscriptions s
                     WHERE s.plan_id = p.id AND s.current_period_end > NOW()
                       AND s.status NOT IN ('pending', 'failed'))                AS live_subscriptions,
                   (SELECT COUNT(*)::int FROM plan_grants g
                     WHERE g.plan_id = p.id AND (g.expires_at IS NULL OR g.expires_at > NOW())) AS grants
            FROM plans p
            WHERE @id::int IS NULL OR p.id = @id
            ORDER BY p.is_active DESC, p.is_public DESC, p.amount, p.id
            """;

        await using var conn = Connect();
        return (await conn.QueryAsync<PlanAdminItem>(sql, new { id })).ToList();
    }

    public async Task<(PlanWriteStatus status, int id)> CreatePlanAsync(PlanUpsertBody b)
    {
        string code = b.code!.Trim();

        await using var conn = Connect();

        // The unique index is case-sensitive but every lookup is lower(code) … LIMIT 1, so
        // "Monthly" next to "monthly" would make checkout pick one of them at random.
        if (await conn.ExecuteScalarAsync<int?>("SELECT id FROM plans WHERE lower(code) = lower(@code)", new { code }) is not null)
            return (PlanWriteStatus.Exists, 0);

        try
        {
            int id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO plans (code, title, tier, kind, interval_unit, interval_count, amount, is_public, is_active)
                VALUES (@code, @title, @tier, @kind, @unit, @count, @amount, @pub, @active)
                RETURNING id
                """, new
            {
                code, title = b.title!.Trim(), tier = TierOf(b), kind = b.kind, unit = b.interval_unit,
                count = b.interval_count, amount = b.amount, pub = b.is_public ?? true, active = b.is_active ?? true
            });
            return (PlanWriteStatus.Ok, id);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (PlanWriteStatus.Exists, 0);   // lost a race with an identical create
        }
    }

    public async Task<PlanWriteStatus> UpdatePlanAsync(int id, PlanUpsertBody b)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var current = await conn.QuerySingleOrDefaultAsync<PlanRow>(
            $"SELECT {PlanCols} FROM plans WHERE id = @id FOR UPDATE", new { id }, tx);
        if (current is null) { await tx.RollbackAsync(); return PlanWriteStatus.NotFound; }

        // A renewal extends the period by the plan's CURRENT interval (BillingService.PeriodEndFrom),
        // while the provider keeps charging on the schedule the subscription was created with. So
        // the interval and kind are frozen while anything still renews or confirms against them:
        // a pending checkout, a live subscription, a past-due one. The price is not — a new price
        // applies to new purchases, and existing recurring charges stay at what the provider holds.
        bool billingChanged = current.kind != b.kind
                           || current.interval_unit != b.interval_unit
                           || current.interval_count != b.interval_count;
        if (billingChanged)
        {
            bool inUse = await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM subscriptions
                               WHERE plan_id = @id AND status IN ('pending', 'active', 'past_due'))
                """, new { id }, tx);
            if (inUse) { await tx.RollbackAsync(); return PlanWriteStatus.InUse; }
        }

        await conn.ExecuteAsync("""
            UPDATE plans SET title = @title, tier = @tier, kind = @kind, interval_unit = @unit,
                             interval_count = @count, amount = @amount, is_public = @pub, is_active = @active
            WHERE id = @id
            """, new
        {
            id, title = b.title!.Trim(), tier = TierOf(b), kind = b.kind, unit = b.interval_unit,
            count = b.interval_count, amount = b.amount,
            pub = b.is_public ?? current.is_public, active = b.is_active ?? current.is_active
        }, tx);

        await tx.CommitAsync();
        return PlanWriteStatus.Ok;
    }

    // ── Admin: grants ───────────────────────────────────────────────────────────

    // Column order = PlanGrantItem's parameter order.
    private const string GrantSelect = """
        SELECT g.user_id, u.username, u.email,
               g.plan_id, p.code AS plan_code, p.title AS plan_title, p.is_public AS plan_is_public,
               g.expires_at, g.created_at, a.username AS granted_by
        FROM plan_grants g
        JOIN users u       ON u.id = g.user_id
        JOIN plans p       ON p.id = g.plan_id
        LEFT JOIN users a  ON a.id = g.granted_by
        """;

    public async Task<IReadOnlyList<PlanGrantItem>?> ListGrantsForUserAsync(string username)
    {
        await using var conn = Connect();
        int? userId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM users WHERE username = @username", new { username });
        if (userId is null) return null;

        return (await conn.QueryAsync<PlanGrantItem>(
            GrantSelect + " WHERE g.user_id = @u ORDER BY p.amount, p.id", new { u = userId })).ToList();
    }

    public async Task<IReadOnlyList<PlanGrantItem>?> ListGrantsForPlanAsync(int planId)
    {
        await using var conn = Connect();
        if (await conn.ExecuteScalarAsync<int?>("SELECT id FROM plans WHERE id = @planId", new { planId }) is null)
            return null;

        return (await conn.QueryAsync<PlanGrantItem>(
            GrantSelect + " WHERE g.plan_id = @planId ORDER BY g.created_at DESC, g.id DESC", new { planId })).ToList();
    }

    public async Task<bool> RevokeGrantAsync(string username, string planCode)
    {
        // Only the right to buy goes. A subscription already bought on the plan is paid for
        // and stays; ending it is a refund or a cancel, not this.
        await using var conn = Connect();
        return await conn.ExecuteAsync("""
            DELETE FROM plan_grants g
            USING users u, plans p
            WHERE g.user_id = u.id AND g.plan_id = p.id
              AND u.username = @username AND lower(p.code) = lower(@planCode)
            """, new { username, planCode }) > 0;
    }
}
