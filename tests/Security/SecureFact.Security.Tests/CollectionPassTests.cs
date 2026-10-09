using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFact.Billing.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Contracts;
using SecureFact.SharedKernel.Results;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The monthly pass with the documents of a month that nobody issued: the overage, the cap per pass and the failures that must not stop the others.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CollectionPassTests(ApiFixture api)
{
    private sealed record PlanRow(Guid Id);

    private sealed record ChargeRow(Guid Id, DateOnly Period, int DocumentsIssued, int? IncludedDocuments, int OverageDocuments, decimal? OverageUnitPrice, decimal OverageAmount, decimal NetAmount, decimal TaxAmount, decimal TotalAmount);

    /// <summary>The count of documents the pass reads, set by the test: what a tenant issued in a month that already passed.</summary>
    private sealed class Documents(Func<Guid, int> count) : IDocumentService
    {
        public Task<int> CountIssuedAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult(count(tenantId));

        public Task<Result<DocumentPreview>> PreviewAsync(PreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<DocumentPreview>> PreviewNoteAsync(NotePreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<DocumentDto>> CreateAsync(string idempotencyKey, CreateDocumentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<DocumentDto>> CreateNoteAsync(string idempotencyKey, CreateNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<DocumentDto>> GetAsync(Guid documentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentDto>> ListAsync(Guid? companyId, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentDto>> ListIssuedAsync(Guid companyId, string documentTypeCode, DateOnly issueDate, int skip, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Rules(Func<Result<decimal>> answer) : IRuleProvider
    {
        public Task<Result<RuleVersionDto>> ResolveAsync(string code, DateOnly asOf, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<decimal>> ResolveDecimalAsync(string code, string property, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult(answer());

        public Task<IReadOnlyList<RuleVersionDto>> ListAsync(DateOnly asOf, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoPlans : IPlanAdministration
    {
        public Task<Result<IReadOnlyList<PlanDto>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Result<IReadOnlyList<PlanDto>>>(Error.Forbidden("SF-AUTH-002", "Operación no permitida", "El catálogo no se puede leer."));

        public Task<Result<PlanDto>> CreateAsync(PlanInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<PlanDto>> UpdateAsync(Guid id, PlanInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<TenantDto>> AssignAsync(SecureFact.SharedKernel.Domain.TenantId tenantId, Guid planId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static DateOnly NextMonth(int months = 1) => LimaCalendar.MonthStart(LimaCalendar.Today(DateTimeOffset.UtcNow)).AddMonths(months);

    private static DateTimeOffset At(int monthsAhead, int day) => LimaCalendar.StartOf(NextMonth(monthsAhead).AddDays(day - 1)).AddHours(12);

    private async Task<(Guid TenantId, PlanRow Plan)> TenantOnPlanAsync(HttpClient admin, bool overage, decimal fee, int? included = null, decimal? unit = null)
    {
        var plan = (await (await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"pass-{Guid.NewGuid():N}"[..16], name = "Plan del pase", allowsOverage = overage }))
            .Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = NextMonth(), monthlyFee = fee, includedDocuments = included, overageUnitPrice = unit })).StatusCode);
        var tenantId = await api.CreateTenantAsync($"Pase {Guid.NewGuid():N}"[..14]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);
        return (tenantId, plan);
    }

    private async Task<CollectionPassResult> PassAsync(DateTimeOffset when, Func<Guid, int>? documents = null, Func<Result<decimal>>? tax = null, bool noCatalogue = false)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("collection pass test");
        var overrides = new List<object> { new Documents(documents ?? (_ => 0)), NullLogger<CollectionPass>.Instance };
        if (tax is not null)
        {
            overrides.Add(new Rules(tax));
        }

        if (noCatalogue)
        {
            overrides.Add(new NoPlans());
        }

        var pass = ActivatorUtilities.CreateInstance<CollectionPass>(scope.ServiceProvider, [.. overrides]);
        return await pass.RunAsync(when, CancellationToken.None);
    }

    private static async Task<List<ChargeRow>> ChargesOfAsync(HttpClient admin, Guid tenantId) =>
        (await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}", ApiFixture.JsonOptions))!;

    [Fact]
    public async Task The_documents_over_what_the_fee_includes_are_charged_one_by_one_with_the_tax_on_the_whole()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, _) = await TenantOnPlanAsync(admin, overage: true, fee: 100m, included: 100, unit: 0.5m);

        await PassAsync(At(2, 2), id => id == tenantId ? 130 : 0);

        var charge = Assert.Single(await ChargesOfAsync(admin, tenantId));
        Assert.Equal((130, 100, 30, 0.5m, 15m, 115m, 20.7m, 135.7m), (charge.DocumentsIssued, charge.IncludedDocuments, charge.OverageDocuments, charge.OverageUnitPrice, charge.OverageAmount, charge.NetAmount, charge.TaxAmount, charge.TotalAmount));
    }

    [Fact]
    public async Task A_pass_charges_at_most_two_years_for_a_tenant_and_the_next_pass_goes_on_where_it_stopped()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, _) = await TenantOnPlanAsync(admin, overage: false, fee: 10m);

        await PassAsync(At(27, 2));
        var first = await ChargesOfAsync(admin, tenantId);
        Assert.Equal(24, first.Count);
        Assert.Equal(NextMonth(), first.Min(c => c.Period));

        await PassAsync(At(27, 3));
        Assert.Equal(26, (await ChargesOfAsync(admin, tenantId)).Count); // the months that closed: the 26 from the first to the one before the current
    }

    [Fact]
    public async Task A_tenant_whose_charge_fails_does_not_stop_the_others_and_a_missing_tax_rate_creates_nothing()
    {
        using var admin = await api.AdminClientAsync();
        var (broken, _) = await TenantOnPlanAsync(admin, overage: false, fee: 50m);
        var (fine, _) = await TenantOnPlanAsync(admin, overage: false, fee: 60m);

        // The count of documents of one tenant throws: its charge is not made, the other one is.
        var pass = await PassAsync(At(2, 2), id => id == broken ? throw new InvalidOperationException("simulated failure") : 0);
        Assert.True(pass.ChargesCreated >= 1);
        Assert.Empty(await ChargesOfAsync(admin, broken));
        Assert.Single(await ChargesOfAsync(admin, fine));

        // Without a tax rate there is nothing to charge with; the next pass, with the rate back, makes the charge.
        var none = await PassAsync(At(2, 3), tax: () => Error.NotFound("SF-RUL-001", "Regla no encontrada", "No hay tasa de IGV."));
        Assert.Equal(0, none.ChargesCreated);
        Assert.Empty(await ChargesOfAsync(admin, broken));
        await PassAsync(At(2, 4));
        Assert.Single(await ChargesOfAsync(admin, broken));
    }

    [Fact]
    public async Task Without_the_plan_catalogue_the_pass_does_nothing()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, _) = await TenantOnPlanAsync(admin, overage: false, fee: 70m);

        var pass = await PassAsync(At(2, 2), noCatalogue: true);

        Assert.Equal(new CollectionPassResult(0, 0, 0), pass);
        Assert.Empty(await ChargesOfAsync(admin, tenantId));
    }

    [Fact]
    public async Task A_free_plan_that_is_tracked_creates_no_charge_and_a_fee_of_zero_with_overage_creates_one_even_when_nothing_is_over()
    {
        using var admin = await api.AdminClientAsync();
        var (free, _) = await TenantOnPlanAsync(admin, overage: false, fee: 0m);
        var (metered, _) = await TenantOnPlanAsync(admin, overage: true, fee: 0m, included: 50, unit: 1m);

        await PassAsync(At(2, 2), id => id == metered ? 80 : 0);

        Assert.Empty(await ChargesOfAsync(admin, free));
        var charge = Assert.Single(await ChargesOfAsync(admin, metered));
        Assert.Equal((30m, 35.4m), (charge.OverageAmount, charge.TotalAmount));
    }
}
