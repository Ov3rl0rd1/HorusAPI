using HorusAPI.Services;
using HorusAPI.Services.Billing;

namespace HorusAPI.Tests.Unit;

/// <summary>The plan body rules the admin endpoints answer 400 with, and the offer summary of the node view.</summary>
public class PlanValidationTests
{
    private static PlanUpsertBody Valid(string? code = "monthly") =>
        new(code, "Месяц", "standard", "recurring", "month", 1, 199, true, true);

    [Fact]
    public void A_complete_body_is_valid() =>
        Assert.Null(PlanService.Validate(Valid(), creating: true));

    [Fact]
    public void An_update_needs_no_code() =>
        Assert.Null(PlanService.Validate(Valid(code: null), creating: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("../etc")]
    public void A_bad_code_is_refused_on_create(string? code) =>
        Assert.NotNull(PlanService.Validate(Valid(code), creating: true));

    [Fact]
    public void A_missing_body_is_refused() =>
        Assert.NotNull(PlanService.Validate(null, creating: false));

    [Fact]
    public void An_empty_tier_falls_back_rather_than_failing() =>
        Assert.Null(PlanService.Validate(Valid() with { tier = null }, creating: true));

    [Theory]
    [InlineData("forever", "month", 1, 199)]     // kind
    [InlineData("recurring", "decade", 1, 199)]  // unit
    [InlineData("recurring", "month", 0, 199)]   // count
    [InlineData("recurring", "month", 366, 199)]
    [InlineData("recurring", "month", 1, 0)]     // amount: whole rubles, at least one
    public void Out_of_range_fields_are_refused(string kind, string unit, int count, int amount) =>
        Assert.NotNull(PlanService.Validate(
            Valid() with { kind = kind, interval_unit = unit, interval_count = count, amount = amount }, creating: false));

    [Fact]
    public void A_blank_title_is_refused() =>
        Assert.NotNull(PlanService.Validate(Valid() with { title = "  " }, creating: false));

    [Fact]
    public void Offers_are_summarised_without_being_modelled()
    {
        var offers = AdminServerService.Summarize("""
            [{"id":"a","outbound":{"protocol":"vless"},"uri":"vless://x"},
             {"id":"b","outbound":["not","an","object"]},
             {"id":"c"}]
            """);

        Assert.Equal(["vless", null, null], offers.Select(o => o.protocol));
        Assert.Equal([true, false, false], offers.Select(o => o.has_uri));
    }

    [Fact]
    public void Unreadable_offers_summarise_to_nothing() =>
        Assert.Empty(AdminServerService.Summarize("{not json"));
}
