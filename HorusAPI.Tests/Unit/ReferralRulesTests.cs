using HorusAPI.Services.Billing;

namespace HorusAPI.Tests.Unit;

/// <summary>The referral programme's pure rules: what a partner body may say, and the money math.</summary>
public class ReferralRulesTests
{
    private static ReferralUpsertBody Body(string? code = "BLOGGER", int? discount = 10, int? reward = 30,
        bool? active = null, string? note = null) => new(code, discount, reward, active, note);

    [Fact]
    public void A_complete_new_partner_is_valid()
    {
        Assert.Null(ReferralService.Validate(Body(), creating: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]                      // too short to read out
    [InlineData("has space")]
    [InlineData("кириллица")]               // travels badly in a link
    [InlineData("a234567890123456789012345678901234")]   // 33 characters
    public void Creating_needs_a_shareable_code(string? code)
    {
        Assert.NotNull(ReferralService.Validate(Body(code: code), creating: true));
    }

    [Fact]
    public void Creating_needs_both_percents()
    {
        Assert.NotNull(ReferralService.Validate(Body(discount: null), creating: true));
        Assert.NotNull(ReferralService.Validate(Body(reward: null), creating: true));
    }

    [Fact]
    public void A_change_may_send_only_what_changes()
    {
        // { is_active: false } is how a partner is switched off.
        Assert.Null(ReferralService.Validate(new ReferralUpsertBody(null, null, null, false, null), creating: false));
    }

    [Fact]
    public void A_change_that_sends_a_code_still_needs_a_valid_one()
    {
        Assert.NotNull(ReferralService.Validate(Body(code: "x"), creating: false));
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(91, 30)]     // a 100% discount would make the customer's purchase free
    [InlineData(10, -1)]
    [InlineData(10, 101)]
    public void Percents_are_bounded(int discount, int reward)
    {
        Assert.NotNull(ReferralService.Validate(Body(discount: discount, reward: reward), creating: true));
    }

    [Fact]
    public void Zero_is_a_valid_discount_and_a_valid_reward()
    {
        // A partner who only earns, or one who only gives a discount ("для своих").
        Assert.Null(ReferralService.Validate(Body(discount: 0, reward: 0), creating: true));
    }

    [Fact]
    public void Notes_are_bounded()
    {
        Assert.NotNull(ReferralService.Validate(Body(note: new string('x', 257)), creating: true));
    }

    [Theory]
    [InlineData(249, 30, 74)]     // 74.7 → 74: the partner is never paid more than earned
    [InlineData(224, 25, 56)]
    [InlineData(1, 30, 0)]
    [InlineData(249, 0, 0)]
    [InlineData(0, 30, 0)]
    [InlineData(249, 100, 249)]
    [InlineData(249, 150, 249)]   // clamped, should a bad value ever get this far
    public void Share_rounds_down_to_whole_rubles(int paid, int percent, int expected)
    {
        Assert.Equal(expected, ReferralService.Share(paid, percent));
    }

    [Fact]
    public void A_large_payment_does_not_overflow_the_share()
    {
        Assert.Equal(1_000_000_000, ReferralService.Share(2_000_000_000, 50));
    }

    [Fact]
    public void The_link_goes_straight_to_sign_up_with_the_code()
    {
        Assert.Equal("https://vpn.example/login?mode=register&ref=BLOGGER",
            ReferralService.LinkFor("https://vpn.example/", "BLOGGER"));
    }

    [Fact]
    public void One_paid_period_has_one_key_whatever_the_time_of_day()
    {
        // The activation and a provider's report of the same first charge carry the same period
        // end, give or take seconds — they must land on one reward, not two.
        var morning = new DateTime(2026, 11, 2, 0, 0, 5, DateTimeKind.Utc);
        var evening = new DateTime(2026, 11, 2, 23, 59, 0, DateTimeKind.Utc);
        Assert.Equal(ReferralService.PeriodSource(7, morning), ReferralService.PeriodSource(7, evening));
        Assert.NotEqual(ReferralService.PeriodSource(7, morning), ReferralService.PeriodSource(7, morning.AddMonths(1)));
        Assert.NotEqual(ReferralService.PeriodSource(7, morning), ReferralService.PeriodSource(8, morning));
    }

    [Fact]
    public void A_referral_discount_never_makes_a_purchase_free()
    {
        // Pricing.Discount is shared with promos: the payer is always charged at least 1 ₽.
        Assert.Equal(1, 2 - Pricing.Discount(2, ReferralService.MaxDiscountPercent));
        Assert.Equal(224, 249 - Pricing.Discount(249, 10));   // 24.9 → 25 ₽ off
    }
}
