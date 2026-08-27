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
    Task<IReadOnlyList<PaymentRow>> ListPaymentsAsync(string? username);
}

public class PlanService(IConfiguration cfg, IEntitlementService entitlement) : IPlanService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    private const string PlanCols =
        "id, code, title, tier, kind, interval_unit, interval_count, amount, currency, is_public, is_active";

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
        return rows.Select(p => new PlanView(
            p.code, p.title, p.tier, p.kind, p.interval_unit, p.interval_count, p.amount, p.currency, p.is_public)).ToList();
    }

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

    public async Task<IReadOnlyList<PaymentRow>> ListPaymentsAsync(string? username)
    {
        const string cols = "id, user_id, plan_id, subscription_id, provider, provider_ref, kind, amount, currency, promo_code_id, discount, status, hold_id";
        await using var conn = Connect();
        if (string.IsNullOrWhiteSpace(username))
        {
            var all = await conn.QueryAsync<PaymentRow>($"SELECT {cols} FROM payments ORDER BY id DESC LIMIT 500");
            return all.ToList();
        }
        var rows = await conn.QueryAsync<PaymentRow>(
            $"SELECT {cols} FROM payments WHERE user_id = (SELECT id FROM users WHERE username = @username) ORDER BY id DESC LIMIT 500",
            new { username });
        return rows.ToList();
    }
}
