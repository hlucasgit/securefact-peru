using System.Globalization;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class SubscriptionEndpoints
{
    public sealed record PriceBody(DateOnly EffectiveFrom, decimal MonthlyFee, int? IncludedDocuments, decimal? OverageUnitPrice, string? Note);

    public sealed record PolicyBody(DateOnly EffectiveFrom, int DueDays, int? SuspendAfterDays, string? Note);

    public sealed record PaymentBody(decimal Amount, PaymentMethod Method, DateOnly PaidOn, string? Reference, string? Note);

    public sealed record ReasonBody(string Reason);

    public sealed record ScheduleBody(DateOnly EffectiveFrom, IReadOnlyList<CommissionTierInput> Tiers, string? Note);

    public sealed record SettleBody(DateOnly SettledOn, string? Reference, string? Note);

    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Subscriptions");

        // ---------- prices and policy (platform staff) ----------

        api.MapGet("/platform/plans/{id:guid}/prices", async (Guid id, IPricing pricing, HttpContext http, CancellationToken ct) =>
            (await pricing.ListPricesAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapPost("/platform/plans/{id:guid}/prices", async (Guid id, PriceBody body, IPricing pricing, HttpContext http, CancellationToken ct) =>
            (await pricing.PublishPriceAsync(id, new PlanPriceInput(body.EffectiveFrom, body.MonthlyFee, body.IncludedDocuments, body.OverageUnitPrice, body.Note), ct))
            .ToHttp(http, dto => Results.Created($"/api/v1/platform/plans/{id}/prices", dto))).RequireAuthorization(Permissions.SubscriptionsManage);

        api.MapGet("/platform/billing-policies", async (IPricing pricing, HttpContext http, CancellationToken ct) =>
            (await pricing.ListPoliciesAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapPost("/platform/billing-policies", async (PolicyBody body, IPricing pricing, HttpContext http, CancellationToken ct) =>
            (await pricing.PublishPolicyAsync(new BillingPolicyInput(body.EffectiveFrom, body.DueDays, body.SuspendAfterDays, body.Note), ct))
            .ToHttp(http, dto => Results.Created("/api/v1/platform/billing-policies", dto))).RequireAuthorization(Permissions.SubscriptionsManage);

        api.MapGet("/platform/tenants/{id:guid}/terms", async (Guid id, ICurrentUser user, IPricing pricing, HttpContext http, CancellationToken ct) =>
            user.IsPlatform ? (await pricing.TermsOfAsync(new TenantId(id), ct)).ToHttp(http) : Results.Forbid()).RequireAuthorization(Permissions.SubscriptionsRead);

        // ---------- charges and payments (platform staff) ----------

        api.MapGet("/platform/charges", async (Guid? tenantId, ChargeStatus? status, string? period, int? skip, int? take, ICollections collections, HttpContext http, CancellationToken ct) =>
            TryPeriod(period, out var month)
                ? (await collections.ListChargesAsync(new ChargeFilter(tenantId, status, month, skip ?? 0, take ?? 50), ct)).ToHttp(http)
                : BadPeriod(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapGet("/platform/charges/{id:guid}", async (Guid id, ICollections collections, HttpContext http, CancellationToken ct) =>
            (await collections.GetChargeAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapPost("/platform/charges/{id:guid}/payments", async (Guid id, PaymentBody body, ICollections collections, HttpContext http, CancellationToken ct) =>
            (await collections.RecordPaymentAsync(id, new RecordPaymentRequest(body.Amount, body.Method, body.PaidOn, body.Reference, body.Note), ct))
            .ToHttp(http, dto => Results.Created($"/api/v1/platform/charges/{id}", dto))).RequireAuthorization(Permissions.SubscriptionsManage);

        api.MapPost("/platform/charges/{id:guid}/void", async (Guid id, ReasonBody body, ICollections collections, HttpContext http, CancellationToken ct) =>
            (await collections.VoidChargeAsync(id, body.Reason, ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsManage);

        api.MapPost("/platform/payments/{id:guid}/reverse", async (Guid id, ReasonBody body, ICollections collections, HttpContext http, CancellationToken ct) =>
            (await collections.ReversePaymentAsync(id, body.Reason, ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsManage);

        // The pass that the workers run on their own, for an operator who wants it now.
        api.MapPost("/platform/subscriptions/run", async (ICollectionProcessor processor, TimeProvider clock, ICurrentUser user, CancellationToken ct) =>
            user.IsPlatform ? Results.Ok(await processor.RunAsync(clock.GetUtcNow(), ct)) : Results.Forbid()).RequireAuthorization(Permissions.SubscriptionsManage);

        // ---------- commissions ----------

        api.MapGet("/platform/commission-schedules", async (ICommissions commissions, HttpContext http, CancellationToken ct) =>
            (await commissions.ListSchedulesAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapPost("/platform/commission-schedules", async (ScheduleBody body, ICommissions commissions, HttpContext http, CancellationToken ct) =>
            (await commissions.PublishScheduleAsync(new CommissionScheduleInput(body.EffectiveFrom, body.Tiers ?? [], body.Note), ct))
            .ToHttp(http, dto => Results.Created("/api/v1/platform/commission-schedules", dto))).RequireAuthorization(Permissions.SubscriptionsManage);

        api.MapGet("/platform/resellers/{id:guid}/commissions", async (Guid id, ICommissions commissions, HttpContext http, CancellationToken ct) =>
            (await commissions.OverviewAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapGet("/platform/resellers/{id:guid}/commissions/{month}", async (Guid id, string month, ICommissions commissions, HttpContext http, CancellationToken ct) =>
            TryPeriod(month, out var first) && first is { } day
                ? (await commissions.StatementAsync(id, day, ct)).ToHttp(http)
                : BadPeriod(http)).RequireAuthorization(Permissions.SubscriptionsRead);

        api.MapPost("/platform/resellers/{id:guid}/commissions/{month}/settle", async (Guid id, string month, SettleBody body, ICommissions commissions, HttpContext http, CancellationToken ct) =>
            TryPeriod(month, out var first) && first is { } day
                ? (await commissions.SettleAsync(id, day, new SettleCommissionRequest(body.SettledOn, body.Reference, body.Note), ct)).ToHttp(http)
                : BadPeriod(http)).RequireAuthorization(Permissions.SubscriptionsManage);

        // ---------- the reseller's own view: the reseller always comes from the token ----------

        api.MapGet("/reseller/commissions", async (ICurrentUser user, ICommissions commissions, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await commissions.OverviewAsync(reseller, ct)).ToHttp(http) : Results.Forbid()).RequireAuthorization(Permissions.ResellerCommissionsRead);

        api.MapGet("/reseller/commissions/{month}", async (string month, ICurrentUser user, ICommissions commissions, HttpContext http, CancellationToken ct) =>
        {
            if (user.ResellerId is not { } reseller)
            {
                return Results.Forbid();
            }

            return TryPeriod(month, out var first) && first is { } day ? (await commissions.StatementAsync(reseller, day, ct)).ToHttp(http) : BadPeriod(http);
        }).RequireAuthorization(Permissions.ResellerCommissionsRead);

        // ---------- the account's own view ----------

        api.MapGet("/subscription", async (ICurrentUser user, IPricing pricing, HttpContext http, CancellationToken ct) =>
            user.TenantId is { } tenant ? (await pricing.TermsOfAsync(tenant, ct)).ToHttp(http) : Results.NotFound()).RequireAuthorization(Permissions.TenantsRead);

        api.MapGet("/charges", async (ChargeStatus? status, string? period, int? skip, int? take, ICurrentUser user, ICollections collections, HttpContext http, CancellationToken ct) =>
            user.TenantId is null ? Results.NotFound()
            : TryPeriod(period, out var month) ? (await collections.ListChargesAsync(new ChargeFilter(null, status, month, skip ?? 0, take ?? 50), ct)).ToHttp(http)
            : BadPeriod(http)).RequireAuthorization(Permissions.TenantsRead);

        api.MapGet("/charges/{id:guid}", async (Guid id, ICurrentUser user, ICollections collections, HttpContext http, CancellationToken ct) =>
            user.TenantId is null ? Results.NotFound() : (await collections.GetChargeAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsRead);
    }

    /// <summary>An empty value is no filter; otherwise the month is written <c>yyyy-MM</c>.</summary>
    private static bool TryPeriod(string? text, out DateOnly? month)
    {
        month = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateOnly.TryParseExact(text.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            month = parsed;
            return true;
        }

        return false;
    }

    private static IResult BadPeriod(HttpContext http) =>
        ResultHttpExtensions.ToProblem(Error.Validation(ErrorCodes.InvalidRequest, "Mes inválido", "El mes se escribe aaaa-mm, por ejemplo 2026-10."), http);
}
