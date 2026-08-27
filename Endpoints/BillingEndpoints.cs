using HorusAPI.Models;
using HorusAPI.Services;
using HorusAPI.Services.Billing;
using Microsoft.AspNetCore.Mvc;

namespace HorusAPI.Endpoints;

/// <summary>
/// Billing surface. User routes (<c>/billing/*</c>) need the session header; the provider
/// webhook (<c>/payments/{provider}/webhook</c>) is anonymous at the framework level and
/// authenticates the caller by the shared secret inside the adapter. Prices and discounts are
/// always computed on the server — the client only ever names a plan/promo code.
/// </summary>
public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/billing")
            .WithTags("Billing")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Billing);

        // Plans available to this user (public + those granted to them).
        group.MapGet("/plans", async (HttpContext ctx, IPlanService plans) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User user) return Results.Unauthorized();
            try { return Results.Ok(await plans.GetPlansForUserAsync(user.id)); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
        })
        .Produces<IReadOnlyList<PlanView>>(200)
        .WithSummary("Plans the caller may buy (public + granted).");

        // Start a checkout — returns the provider redirect URL to send the payer to.
        group.MapPost("/checkout", async (
            [FromBody] CheckoutBody body, HttpContext ctx, IBillingService billing) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User user) return Results.Unauthorized();

            CheckoutResult res;
            try { res = await billing.CheckoutAsync(user, body ?? new CheckoutBody(null, null)); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return res.Status switch
            {
                CheckoutStatus.Ok                 => Results.Ok(res.View),
                CheckoutStatus.PlanNotFound       => Results.NotFound(new ApiError("Plan not available.", "plan_not_found")),
                CheckoutStatus.PromoInvalid       => Results.BadRequest(new ApiError($"Promo code is not valid ({res.Detail}).", "promo_invalid")),
                CheckoutStatus.PromoNotApplicable => Results.BadRequest(new ApiError("Promo codes don't apply to subscriptions.", "promo_not_applicable")),
                CheckoutStatus.NoCapacity         => Results.Json(new ApiError("No free slots available.", "no_capacity"), statusCode: 409),
                _                                 => Results.Json(new ApiError("Payment provider error.", "provider_error"), statusCode: 502),
            };
        })
        .Produces<CheckoutView>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(404)
        .Produces<ApiError>(409)
        .Produces<ApiError>(502)
        .WithSummary("Create a payment (recurring or one-time) and get the provider redirect URL.");

        // The caller's current subscription/state.
        group.MapGet("/subscription", async (HttpContext ctx, IBillingService billing) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User user) return Results.Unauthorized();
            SubscriptionView? view;
            try { view = await billing.GetSubscriptionAsync(user.id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }
            return Results.Ok(view ?? new SubscriptionView("none", null, null, false, ""));
        })
        .Produces<SubscriptionView>(200)
        .WithSummary("The caller's current subscription (status 'none' when there is none).");

        // Turn off auto-renew (access continues until the period ends).
        group.MapPost("/cancel", async (HttpContext ctx, IBillingService billing) =>
        {
            if (ctx.Items[ApiConsts.UserHttpContext] is not User user) return Results.Unauthorized();
            CancelStatus status;
            try { status = await billing.CancelAsync(user.id); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return status switch
            {
                CancelStatus.Ok       => Results.NoContent(),
                CancelStatus.NotFound => Results.NotFound(new ApiError("No active subscription to cancel.", "no_subscription")),
                _                     => Results.Json(new ApiError("Payment provider error.", "provider_error"), statusCode: 502),
            };
        })
        .Produces(204)
        .Produces<ApiError>(404)
        .Produces<ApiError>(502)
        .WithSummary("Cancel auto-renew; access remains until the current period ends.");

        // ── Provider webhook ─────────────────────────────────────────────────────────
        // Anonymous: authenticity is the shared secret, checked inside the adapter. Always
        // answers fast; a processing failure returns 500 so the provider retries.
        app.MapPost("/payments/{provider}/webhook", async (
            [FromRoute] string provider,
            HttpContext ctx,
            IPaymentProvider paymentProvider,
            IBillingService billing,
            ILogger<Program> log) =>
        {
            if (!string.Equals(provider, paymentProvider.Name, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            if (!paymentProvider.VerifyWebhook(ctx.Request.Headers))
            {
                log.LogWarning("Webhook rejected: bad or missing secret");
                return Results.Unauthorized();
            }

            string raw;
            using (var reader = new StreamReader(ctx.Request.Body))
                raw = await reader.ReadToEndAsync();

            PaymentEvent? ev = paymentProvider.ParseWebhook(raw);
            if (ev is null)
            {
                log.LogWarning("Webhook body could not be parsed; acknowledging to stop retries");
                return Results.Ok();   // unparseable will never succeed on retry
            }
            if (ev.Kind == PaymentEventKind.Unknown)
                return Results.Ok();   // e.g. PENDING — nothing to do

            try { await billing.HandleWebhookAsync(ev); }
            catch { return Results.Problem("Webhook processing failed.", statusCode: 500); }  // provider will retry

            return Results.Ok();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Webhook)
        .WithTags("Billing")
        .Produces(200)
        .Produces(401)
        .WithSummary("Payment provider callback (secret-authenticated, idempotent).");
    }
}
