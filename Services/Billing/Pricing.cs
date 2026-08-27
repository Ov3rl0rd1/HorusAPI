namespace HorusAPI.Services.Billing;

/// <summary>
/// Money math for checkout. Everything is WHOLE RUBLES (integers) — Platega charges in
/// whole rubles, so a percent discount is rounded to the nearest ruble and clamped so the
/// payer is always charged at least 1 ₽ (a 0 ₽ charge is meaningless to the acquirer).
/// </summary>
public static class Pricing
{
    /// <summary>Rubles taken off <paramref name="amount"/> for a <paramref name="percent"/>-off promo.</summary>
    public static int Discount(int amount, int percent) =>
        Math.Clamp((int)Math.Round(amount * percent / 100.0, MidpointRounding.AwayFromZero), 0, Math.Max(0, amount - 1));

    /// <summary>Amount actually charged after the discount (never below 1 ₽ for a positive price).</summary>
    public static int Net(int amount, int percent) => amount - Discount(amount, percent);
}
