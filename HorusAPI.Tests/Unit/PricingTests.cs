using HorusAPI.Services.Billing;

namespace HorusAPI.Tests.Unit;

public class PricingTests
{
    [Theory]
    [InlineData(100, 20, 20, 80)]   // clean percentage
    [InlineData(199, 33, 66, 133)]  // rounds to nearest ruble (65.67 → 66)
    [InlineData(100, 100, 99, 1)]   // 100% clamps: payer is still charged 1 ₽
    [InlineData(1, 50, 0, 1)]       // tiny price: discount rounds/clamps to 0
    [InlineData(0, 50, 0, 0)]       // free plan stays free
    [InlineData(300, 10, 30, 270)]
    public void Discount_and_net_are_whole_rubles(int amount, int percent, int expectedDiscount, int expectedNet)
    {
        Assert.Equal(expectedDiscount, Pricing.Discount(amount, percent));
        Assert.Equal(expectedNet, Pricing.Net(amount, percent));
    }

    [Fact]
    public void Net_never_drops_below_one_ruble_for_a_paid_plan()
    {
        for (int pct = 1; pct <= 100; pct++)
            Assert.True(Pricing.Net(500, pct) >= 1);
    }
}
