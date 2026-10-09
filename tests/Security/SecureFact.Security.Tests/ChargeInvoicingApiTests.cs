using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The invoices that the platform issues for what it charges (ADR-065), issued by the Billing module with the account that the platform configures.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ChargeInvoicingApiTests(ApiFixture api)
{
    private static int _rucCounter = 17_000_000;

    private sealed record Issuer(Guid TenantId, HttpClient Owner, string OwnerEmail, CompanyDto Company, Guid Invoice, Guid Receipt, Guid InvoiceNote, Guid ReceiptNote);

    private sealed record PlanRow(Guid Id);

    private sealed record ChargeRow(Guid Id, decimal TotalAmount, string Status, string? Invoice);

    private sealed record DetailRow(ChargeRow Charge, List<DocumentView> Documents);

    private sealed record DocumentView(Guid Id, string Kind, string DocumentTypeCode, string Series, long Number, decimal Total, string? State);

    private sealed record SettingsRow(Guid IssuerTenantId, Guid CompanyId, bool Enabled);

    private sealed record ProfileRow(Guid TenantId, string DocumentTypeCode, string DocumentNumber, string LegalName);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PlatformStaff : SecureFact.SharedKernel.Tenancy.ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => null;

        public Guid? SessionId => null;

        public SecureFact.SharedKernel.Domain.TenantId? TenantId => null;

        public bool IsPlatform => true;

        public Guid? ResellerId => null;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

        public bool HasPermission(string permission) => true;
    }

    /// <summary>What the pass reads of the documents of a tenant: how many it issued in a month that already passed. The pass reads nothing else of them.</summary>
    private sealed class Documents(Func<Guid, int> count) : IDocumentService
    {
        public Task<int> CountIssuedAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult(count(tenantId));

        public Task<SecureFact.SharedKernel.Results.Result<DocumentPreview>> PreviewAsync(PreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecureFact.SharedKernel.Results.Result<DocumentPreview>> PreviewNoteAsync(NotePreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecureFact.SharedKernel.Results.Result<DocumentDto>> CreateAsync(string idempotencyKey, CreateDocumentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecureFact.SharedKernel.Results.Result<DocumentDto>> CreateNoteAsync(string idempotencyKey, CreateNoteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecureFact.SharedKernel.Results.Result<DocumentDto>> GetAsync(Guid documentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentDto>> ListAsync(Guid? companyId, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentDto>> ListIssuedAsync(Guid companyId, string documentTypeCode, DateOnly issueDate, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static string PfxFor(string ruc)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    private static DateOnly NextMonth(int months = 1) => LimaCalendar.MonthStart(LimaCalendar.Today(DateTimeOffset.UtcNow)).AddMonths(months);

    private static DateTimeOffset At(int monthsAhead, int day) => LimaCalendar.StartOf(NextMonth(monthsAhead).AddDays(day - 1)).AddHours(12);

    /// <summary>The pass as it would run at <paramref name="when"/>: the services that issue the invoices believe it too.</summary>
    private async Task<CollectionPassResult> RunAsync(DateTimeOffset when)
    {
        using var _ = api.Clock.At(when);
        return await api.Services.GetRequiredService<ICollectionProcessor>().RunAsync(when, CancellationToken.None);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>The account of the platform that invoices: a company with its certificate, the invoice, receipt and credit note series, and the settings that name them.</summary>
    private async Task<Issuer> NewIssuerAsync(HttpClient admin, bool enable = true)
    {
        var tenantId = await api.CreateTenantAsync($"Plataforma {Guid.NewGuid():N}"[..16]);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "SECUREFACT PERU SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } })).Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-plataforma-7" })).StatusCode);

        async Task<Guid> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!.Id;

        var issuer = new Issuer(tenantId, owner, user.Email, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"), await SeriesAsync("07", "BC01"));
        Assert.Equal(HttpStatusCode.OK, (await SetSettingsAsync(admin, issuer, enable)).StatusCode);
        return issuer;
    }

    private static Task<HttpResponseMessage> SetSettingsAsync(HttpClient admin, Issuer issuer, bool enabled) =>
        admin.PutAsJsonAsync("/api/v1/platform/invoicing", new
        {
            issuerTenantId = issuer.TenantId,
            companyId = issuer.Company.Id,
            invoiceSeriesId = issuer.Invoice,
            receiptSeriesId = issuer.Receipt,
            invoiceNoteSeriesId = issuer.InvoiceNote,
            receiptNoteSeriesId = issuer.ReceiptNote,
            enabled,
        });

    private async Task<(Guid TenantId, HttpClient Owner)> CustomerAsync(HttpClient admin, decimal fee = 100m, string name = "Cliente de la plataforma")
    {
        var plan = (await (await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"inv-{Guid.NewGuid():N}"[..16], name = "Plan facturado" })).Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = NextMonth(), monthlyFee = fee })).StatusCode);
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..24]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)));
    }

    private static Task<HttpResponseMessage> SetProfileAsync(HttpClient client, string type = "6", string number = "20100066603", string name = "Cliente Facturado SAC") =>
        client.PutAsJsonAsync("/api/v1/billing-profile", new { documentTypeCode = type, documentNumber = number, legalName = name, address = "Jr. Cusco 456", email = "facturas@cliente.test" });

    private static async Task<ChargeRow> ChargeAsync(HttpClient admin, Guid tenantId) =>
        Assert.Single((await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}", ApiFixture.JsonOptions))!);

    private static async Task<DocumentDto> IssuedAsync(Issuer issuer, string name)
    {
        var list = (await issuer.Owner.GetFromJsonAsync<List<DocumentDto>>($"/api/v1/documents?companyId={issuer.Company.Id}&take=100", ApiFixture.JsonOptions))!;
        return Assert.Single(list, d => $"{d.Series}-{d.Number}" == name);
    }

    [Fact]
    public async Task The_invoicing_account_is_configured_by_the_super_admin_with_series_that_suit_what_they_are_for()
    {
        using var admin = await api.AdminClientAsync();
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var issuer = await NewIssuerAsync(admin, enable: false);

        var settings = (await admin.GetFromJsonAsync<SettingsRow>("/api/v1/platform/invoicing", ApiFixture.JsonOptions))!;
        Assert.Equal((issuer.TenantId, issuer.Company.Id, false), (settings.IssuerTenantId, settings.CompanyId, settings.Enabled));
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync("/api/v1/platform/invoicing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetSettingsAsync(supportClient, issuer, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetSettingsAsync(issuer.Owner, issuer, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await issuer.Owner.GetAsync("/api/v1/platform/invoicing")).StatusCode);

        var options = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/platform/invoicing/options?tenantId={issuer.TenantId}", ApiFixture.JsonOptions))!;
        Assert.Equal(4, Assert.Single(options).GetProperty("series").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/platform/invoicing/options?tenantId={Guid.NewGuid()}")).StatusCode);

        // A series of the wrong type or with the wrong letter, one repeated for another purpose, or a company that is not the account's, is refused.
        async Task<string> TryAsync(object body)
        {
            var response = await admin.PutAsJsonAsync("/api/v1/platform/invoicing", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return await CodeAsync(response);
        }

        object With(Guid? invoice = null, Guid? receipt = null, Guid? invoiceNote = null, Guid? receiptNote = null, Guid? company = null) => new
        {
            issuerTenantId = issuer.TenantId,
            companyId = company ?? issuer.Company.Id,
            invoiceSeriesId = invoice ?? issuer.Invoice,
            receiptSeriesId = receipt ?? issuer.Receipt,
            invoiceNoteSeriesId = invoiceNote ?? issuer.InvoiceNote,
            receiptNoteSeriesId = receiptNote ?? issuer.ReceiptNote,
            enabled = true,
        };

        Assert.Equal("SF-SUB-009", await TryAsync(With(invoice: issuer.Receipt)));
        Assert.Equal("SF-SUB-009", await TryAsync(With(receipt: issuer.Invoice)));
        Assert.Equal("SF-SUB-009", await TryAsync(With(invoiceNote: issuer.ReceiptNote)));
        Assert.Equal("SF-SUB-009", await TryAsync(With(receiptNote: issuer.InvoiceNote)));
        Assert.Equal("SF-SUB-009", await TryAsync(With(invoice: Guid.NewGuid())));
        Assert.Equal("SF-SUB-009", await TryAsync(With(company: Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/platform/invoicing", new { issuerTenantId = Guid.NewGuid(), companyId = issuer.Company.Id, invoiceSeriesId = issuer.Invoice, receiptSeriesId = issuer.Receipt, invoiceNoteSeriesId = issuer.InvoiceNote, receiptNoteSeriesId = issuer.ReceiptNote, enabled = true })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetSettingsAsync(admin, issuer, true)).StatusCode);
        Assert.True((await admin.GetFromJsonAsync<SettingsRow>("/api/v1/platform/invoicing", ApiFixture.JsonOptions))!.Enabled);
    }

    [Fact]
    public async Task A_tenant_gives_its_billing_data_and_they_are_checked_and_private()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner) = await CustomerAsync(admin);
        var (otherId, other) = await CustomerAsync(admin);
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/billing-profile")).StatusCode);
        foreach (var (type, number, name) in new[] { ("6", "20100066604", "Cliente"), ("6", "123", "Cliente"), ("1", "1234567", "Persona"), ("4", "12345678", "Persona"), ("6", "20100066603", "ab") })
        {
            var refused = await SetProfileAsync(owner, type, number, name);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-SUB-008", await CodeAsync(refused));
        }

        Assert.Equal("SF-SUB-008", await CodeAsync(await owner.PutAsJsonAsync("/api/v1/billing-profile", new { documentTypeCode = "6", documentNumber = "20100066603", legalName = "Cliente SAC", email = "sin arroba" })));

        var saved = await SetProfileAsync(owner);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(("6", "20100066603"), ((await saved.Content.ReadFromJsonAsync<ProfileRow>(ApiFixture.JsonOptions))!.DocumentTypeCode, "20100066603"));
        Assert.Equal(HttpStatusCode.OK, (await SetProfileAsync(owner, "1", "12345678", "Persona Natural")).StatusCode); // it can be changed
        Assert.Equal("12345678", (await owner.GetFromJsonAsync<ProfileRow>("/api/v1/billing-profile", ApiFixture.JsonOptions))!.DocumentNumber);

        // A tenant sees only its own; platform staff read any and the super administrator edits any; support does not edit.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/billing-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/platform/tenants/{otherId}/billing-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/v1/platform/tenants/{otherId}/billing-profile", new { documentTypeCode = "1", documentNumber = "12345678", legalName = "Intruso" })).StatusCode);
        Assert.Equal("12345678", (await supportClient.GetFromJsonAsync<ProfileRow>($"/api/v1/platform/tenants/{tenantId}/billing-profile", ApiFixture.JsonOptions))!.DocumentNumber);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PutAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/billing-profile", new { documentTypeCode = "1", documentNumber = "12345678", legalName = "Soporte" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/tenants/{otherId}/billing-profile", new { documentTypeCode = "6", documentNumber = "20100066603", legalName = "Cargado por la plataforma SAC" })).StatusCode);
        Assert.Equal("Cargado por la plataforma SAC", (await other.GetFromJsonAsync<ProfileRow>("/api/v1/billing-profile", ApiFixture.JsonOptions))!.LegalName);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/billing-profile")).StatusCode);

        // The role that edits is not every role of the tenant: a reader does not.
        var reader = await ApiFixture.CreateUserAsync(admin, Roles.ReadOnly, tenantId);
        using var readerClient = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await SetProfileAsync(readerClient)).StatusCode);
    }

    [Fact]
    public async Task A_charge_is_invoiced_to_a_company_on_credit_with_the_amounts_of_the_charge_and_to_a_person_with_a_receipt()
    {
        using var admin = await api.AdminClientAsync();
        var issuer = await NewIssuerAsync(admin);
        var (companyTenant, companyOwner) = await CustomerAsync(admin, 100m);
        var (personTenant, personOwner) = await CustomerAsync(admin, 40m);
        Assert.Equal(HttpStatusCode.OK, (await SetProfileAsync(companyOwner)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetProfileAsync(personOwner, "1", "12345678", "María Pérez")).StatusCode);

        await RunAsync(At(2, 2));

        var companyCharge = await ChargeAsync(admin, companyTenant);
        var personCharge = await ChargeAsync(admin, personTenant);
        Assert.Equal(("F001-1", 118m), (companyCharge.Invoice, companyCharge.TotalAmount));
        Assert.Equal(("B001-1", 47.2m), (personCharge.Invoice, personCharge.TotalAmount));

        var invoice = await IssuedAsync(issuer, "F001-1");
        Assert.Equal(("01", 118m, "20100066603", "Cliente Facturado SAC"), (invoice.DocumentTypeCode, invoice.Totals.PayableAmount, invoice.Buyer.DocumentNumber, invoice.Buyer.Name));
        Assert.Equal(LimaCalendar.Today(At(2, 2)), invoice.IssueDate);
        var line = Assert.Single(invoice.Lines);
        Assert.Contains("plan Plan facturado", line.Description, StringComparison.Ordinal);
        // The invoice is a credit sale: one installment with the due date of the charge, for the total.
        var installment = Assert.Single(invoice.Installments!);
        Assert.Equal((118m, LimaCalendar.Today(At(2, 2)).AddDays(10)), (installment.Amount, installment.DueDate));

        var receipt = await IssuedAsync(issuer, "B001-1");
        Assert.Equal(("03", 47.2m, "12345678"), (receipt.DocumentTypeCode, receipt.Totals.PayableAmount, receipt.Buyer.DocumentNumber));
        Assert.Null(receipt.Installments);

        // The customer sees the number of its invoice and its link, and the platform does too.
        var detail = (await companyOwner.GetFromJsonAsync<DetailRow>($"/api/v1/charges/{companyCharge.Id}", ApiFixture.JsonOptions))!;
        var document = Assert.Single(detail.Documents);
        Assert.Equal(("Invoice", "01", "F001", 1L, 118m), (document.Kind, document.DocumentTypeCode, document.Series, document.Number, document.Total));
        Assert.Equal("F001-1", Assert.Single((await companyOwner.GetFromJsonAsync<List<ChargeRow>>("/api/v1/charges", ApiFixture.JsonOptions))!).Invoice);
        Assert.Equal(HttpStatusCode.NotFound, (await companyOwner.GetAsync($"/api/v1/charges/{personCharge.Id}")).StatusCode);

        // Passes again issue nothing more, even with other processes at the same time.
        await Task.WhenAll(RunAsync(At(2, 3)), RunAsync(At(2, 3)));
        Assert.Equal("F001-1", (await ChargeAsync(admin, companyTenant)).Invoice);
        Assert.Equal(2, (await issuer.Owner.GetFromJsonAsync<List<DocumentDto>>($"/api/v1/documents?companyId={issuer.Company.Id}&take=100", ApiFixture.JsonOptions))!.Count);
    }

    [Fact]
    public async Task A_charge_waits_for_the_billing_data_and_is_invoiced_when_they_arrive_and_nothing_is_issued_while_the_platform_is_not_set_up()
    {
        using var admin = await api.AdminClientAsync();
        var issuer = await NewIssuerAsync(admin, enable: false);
        var (tenantId, owner) = await CustomerAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await SetProfileAsync(owner)).StatusCode);

        // Disabled: the charge is made and collected, not invoiced.
        await RunAsync(At(2, 2));
        Assert.Null((await ChargeAsync(admin, tenantId)).Invoice);
        Assert.Empty((await issuer.Owner.GetFromJsonAsync<List<DocumentDto>>($"/api/v1/documents?companyId={issuer.Company.Id}", ApiFixture.JsonOptions))!);

        // Enabled, the next pass invoices it.
        Assert.Equal(HttpStatusCode.OK, (await SetSettingsAsync(admin, issuer, true)).StatusCode);
        var pass = await RunAsync(At(2, 3));
        Assert.True(pass.InvoicesIssued >= 1);
        Assert.Equal("F001-1", (await ChargeAsync(admin, tenantId)).Invoice);

        // Without data a charge is not invoiced; with them it is.
        var (noDataTenant, noDataOwner) = await CustomerAsync(admin, 60m);
        await RunAsync(At(2, 4));
        Assert.Null((await ChargeAsync(admin, noDataTenant)).Invoice);
        await SetProfileAsync(noDataOwner, "6", "20100066603", "Tardío SAC");
        await RunAsync(At(2, 5));
        Assert.Equal("F001-2", (await ChargeAsync(admin, noDataTenant)).Invoice);
    }

    [Fact]
    public async Task A_charge_with_overage_is_invoiced_in_two_lines_that_add_up_to_the_same_total()
    {
        using var admin = await api.AdminClientAsync();
        var issuer = await NewIssuerAsync(admin);
        var plan = (await (await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"inv-{Guid.NewGuid():N}"[..16], name = "Plan medido", allowsOverage = true })).Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = NextMonth(), monthlyFee = 33.33m, includedDocuments = 100, overageUnitPrice = 0.085m })).StatusCode);
        var tenantId = await api.CreateTenantAsync($"Medida {Guid.NewGuid():N}"[..20]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        using var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        Assert.Equal(HttpStatusCode.OK, (await SetProfileAsync(owner)).StatusCode);

        // The pass with the documents of the month that nobody issued: 233 are 133 over, 11.31 soles.
        using (api.Clock.At(At(2, 2)))
        {
            await using var scope = api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("charge invoicing test");
            var pass = ActivatorUtilities.CreateInstance<CollectionPass>(scope.ServiceProvider, new Documents(id => id == tenantId ? 233 : 0), NullLogger<CollectionPass>.Instance);
            await pass.RunAsync(At(2, 2), CancellationToken.None);
        }

        var charge = await ChargeAsync(admin, tenantId);
        Assert.Equal(52.68m, charge.TotalAmount);
        var invoice = await IssuedAsync(issuer, charge.Invoice!);
        Assert.Equal(2, invoice.Lines.Count);
        Assert.Equal([33.33m, 11.31m], invoice.Lines.Select(l => l.LineExtensionAmount));
        Assert.Equal(charge.TotalAmount, invoice.Totals.PayableAmount);
        Assert.Contains("133", invoice.Lines[1].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voiding_an_invoiced_charge_issues_the_credit_note_that_cancels_it()
    {
        using var admin = await api.AdminClientAsync();
        var issuer = await NewIssuerAsync(admin);
        var (tenantId, owner) = await CustomerAsync(admin);
        await SetProfileAsync(owner);
        await RunAsync(At(2, 2));
        var charge = await ChargeAsync(admin, tenantId);
        Assert.Equal("F001-1", charge.Invoice);

        // Without the platform set up the charge cannot be voided: nothing would cancel its invoice.
        Assert.Equal(HttpStatusCode.OK, (await SetSettingsAsync(admin, issuer, false)).StatusCode);
        var refused = await admin.PostAsJsonAsync($"/api/v1/platform/charges/{charge.Id}/void", new { reason = "Cargo duplicado" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("SF-SUB-010", await CodeAsync(refused));
        Assert.Equal("Pending", (await ChargeAsync(admin, tenantId)).Status);

        Assert.Equal(HttpStatusCode.OK, (await SetSettingsAsync(admin, issuer, true)).StatusCode);
        // The void is made by the service as it would be on that day (an HTTP call cannot: the sessions are of today).
        using (api.Clock.At(At(2, 3)))
        {
            await using var scope = api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("charge invoicing test");
            var clock = new FixedClock(At(2, 3));
            var commissions = ActivatorUtilities.CreateInstance<Commissions>(scope.ServiceProvider, (TimeProvider)clock, (SecureFact.SharedKernel.Tenancy.ICurrentUser)new PlatformStaff());
            var collections = ActivatorUtilities.CreateInstance<Collections>(scope.ServiceProvider, (TimeProvider)clock, commissions);
            Assert.True((await collections.VoidChargeAsync(charge.Id, "Cargo duplicado", CancellationToken.None)).IsSuccess);
        }


        var detail = (await admin.GetFromJsonAsync<DetailRow>($"/api/v1/platform/charges/{charge.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(["Invoice", "CreditNote"], detail.Documents.Select(d => d.Kind));
        var note = detail.Documents[1];
        Assert.Equal(("07", "FC01", 1L, 118m), (note.DocumentTypeCode, note.Series, note.Number, note.Total));
        var issued = await IssuedAsync(issuer, "FC01-1");
        Assert.Equal("F001-1", $"{issued.Note!.ReferencedSeries}-{issued.Note.ReferencedNumber}");
        Assert.Equal(("01", "Anulación de la operación: el cargo fue anulado"), (issued.Note.ReasonCode, issued.Note.Reason));
        Assert.Equal("Void", (await ChargeAsync(admin, tenantId)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/platform/charges/{charge.Id}/void", new { reason = "Otra vez" })).StatusCode);
    }

    [Fact]
    public async Task The_customer_downloads_the_invoice_of_its_charge_and_nobody_else_does()
    {
        using var admin = await api.AdminClientAsync();
        var issuer = await NewIssuerAsync(admin);
        var (tenantId, owner) = await CustomerAsync(admin);
        var (_, other) = await CustomerAsync(admin);
        await SetProfileAsync(owner);
        await RunAsync(At(2, 2));
        var charge = await ChargeAsync(admin, tenantId);

        // Until the document is prepared there is no file to give.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/charges/{charge.Id}/documents/Invoice/pdf")).StatusCode);
        var issued = await IssuedAsync(issuer, "F001-1");
        using (api.Clock.At(At(2, 3)))
        {
            await using var scope = api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new SecureFact.SharedKernel.Domain.TenantId(issuer.TenantId));
            Assert.True((await scope.ServiceProvider.GetRequiredService<SecureFact.CpeEngine.Contracts.IElectronicDocumentService>().PrepareAsync(issued.Id, CancellationToken.None)).IsSuccess);
        }


        var pdf = await owner.GetAsync($"/api/v1/charges/{charge.Id}/documents/Invoice/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync())[..4], StringComparison.Ordinal);
        var xml = await owner.GetAsync($"/api/v1/charges/{charge.Id}/documents/Invoice/xml");
        Assert.Equal(HttpStatusCode.OK, xml.StatusCode);
        Assert.Contains("<cbc:ID>F001-1</cbc:ID>", await xml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/platform/charges/{charge.Id}/documents/Invoice/pdf")).StatusCode);
        var detail = (await owner.GetFromJsonAsync<DetailRow>($"/api/v1/charges/{charge.Id}", ApiFixture.JsonOptions))!;
        Assert.False(string.IsNullOrEmpty(Assert.Single(detail.Documents).State)); // the state at SUNAT of the electronic document

        // Another tenant gets the same answer as for a document that does not exist; there is no credit note to download.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/charges/{charge.Id}/documents/Invoice/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/charges/{charge.Id}/documents/CreditNote/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/platform/charges/{charge.Id}/documents/Invoice/pdf")).StatusCode);
    }
}
