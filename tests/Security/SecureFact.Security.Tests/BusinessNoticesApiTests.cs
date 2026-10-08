using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Application;
using SecureFact.Notifications.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The notices about the business of an account (ADR-055): the use of its plan, its digital certificate about to expire, and each fact told once.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class BusinessNoticesApiTests(ApiFixture api)
{
    private static int _rucCounter = 7_000_000;

    private sealed record PlanRow(Guid Id);

    private sealed record Setup(Guid TenantId, string OwnerEmail, HttpClient Owner, CompanyDto Company, string Ruc);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private async Task<Setup> NewTenantAsync(string name, int? documents = null)
    {
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..28]);
        using var admin = await api.AdminClientAsync();
        if (documents is { } limit)
        {
            var plan = await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"plan-{Guid.NewGuid():N}"[..16], name = "Plan con aviso", maxDocumentsPerMonth = limit });
            var row = (await plan.Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = row.Id })).StatusCode);
        }

        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        return new Setup(tenantId, user.Email, owner, company, ruc);
    }

    private static string Pfx(string ruc, int validToDays)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA DEMO SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(validToDays));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    // ---------- plan usage ----------

    [Fact]
    public async Task The_owners_are_told_once_when_the_account_reaches_four_fifths_and_all_of_its_monthly_allowance()
    {
        var setup = await NewTenantAsync("Aviso de plan", documents: 5);
        var series = (await (await setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = setup.Company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        async Task<HttpResponseMessage> IssueAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
            {
                Content = JsonContent.Create(new
                {
                    seriesId = series.Id,
                    issueDate = DateTimeOffset.UtcNow.AddHours(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    currency = "PEN",
                    buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                    lines = new[] { new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
                }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return await setup.Owner.SendAsync(request);
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await IssueAsync()).StatusCode);
        }

        await api.DrainMailAsync();
        Assert.DoesNotContain(api.Mail.To(setup.OwnerEmail), m => m.Subject.Contains("límite", StringComparison.Ordinal)); // 3 of 5 is 60 %: nothing yet

        Assert.Equal(HttpStatusCode.Created, (await IssueAsync()).StatusCode); // the 4th is 80 %
        await api.DrainMailAsync();
        var near = Assert.Single(api.Mail.To(setup.OwnerEmail), m => m.Subject.Contains("por llegar al límite", StringComparison.Ordinal));
        Assert.Contains("4 de los 5 comprobantes", near.Text, StringComparison.Ordinal);
        Assert.Contains("80 %", near.Text, StringComparison.Ordinal);
        Assert.Contains($"{ApiFixture.PublicUrl}/plan", near.Text, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Created, (await IssueAsync()).StatusCode); // the 5th takes the last place
        await api.DrainMailAsync();
        var full = Assert.Single(api.Mail.To(setup.OwnerEmail), m => m.Subject.Contains("llegó al límite", StringComparison.Ordinal));
        Assert.Contains("no podrá emitir más", full.Text, StringComparison.Ordinal);

        // A refused attempt and a replay tell nothing more.
        Assert.Equal(HttpStatusCode.Forbidden, (await IssueAsync()).StatusCode);
        await api.DrainMailAsync();
        Assert.Equal(2, api.Mail.To(setup.OwnerEmail).Count(m => m.Subject.Contains("límite", StringComparison.Ordinal)));
    }

    // ---------- certificates ----------

    private async Task RunExpiryAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICertificateExpiryNotices>().RunAsync(CancellationToken.None);
        await api.DrainMailAsync();
    }

    [Fact]
    public async Task A_certificate_close_to_expiring_is_told_at_the_threshold_it_crossed_and_never_twice()
    {
        var setup = await NewTenantAsync("Aviso de certificado");
        Assert.Equal(HttpStatusCode.Created, (await setup.Owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = setup.Company.Id, pfxBase64 = Pfx(setup.Ruc, validToDays: 12), password = "pw" })).StatusCode);

        await RunExpiryAsync();
        var notice = Assert.Single(api.Mail.To(setup.OwnerEmail), m => m.Subject.Contains("vence en", StringComparison.Ordinal));
        Assert.Contains("vence en 12 días", notice.Subject, StringComparison.Ordinal); // 12 days left: the threshold of 15 was crossed, the one of 30 is not told as well
        Assert.Contains("Representante Demo", notice.Text, StringComparison.Ordinal);
        Assert.Contains($"{ApiFixture.PublicUrl}/empresas", notice.Text, StringComparison.Ordinal);

        await RunExpiryAsync();
        await RunExpiryAsync();
        Assert.Single(api.Mail.To(setup.OwnerEmail), m => m.Subject.Contains("vence en", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_certificate_that_is_far_from_expiring_or_forgotten_long_ago_is_not_told()
    {
        var far = await NewTenantAsync("Certificado lejano");
        Assert.Equal(HttpStatusCode.Created, (await far.Owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = far.Company.Id, pfxBase64 = Pfx(far.Ruc, validToDays: 200), password = "pw" })).StatusCode);

        await RunExpiryAsync();

        Assert.DoesNotContain(api.Mail.To(far.OwnerEmail), m => m.Subject.Contains("certificado", StringComparison.Ordinal));
    }

    // ---------- the fact is told once ----------

    [Fact]
    public async Task The_same_fact_is_queued_once_per_address_whatever_the_number_of_times_it_is_raised()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutbox>();
        var fact = $"hecho:{Guid.NewGuid():N}";
        var one = $"{Guid.NewGuid():N}@dedupe.test";
        var two = $"{Guid.NewGuid():N}@dedupe.test";

        Assert.True(await outbox.EnqueueAsync(new EmailMessage(one, "Aviso", "t", "<p>t</p>"), fact));
        Assert.False(await outbox.EnqueueAsync(new EmailMessage(one, "Aviso otra vez", "t", "<p>t</p>"), fact));
        Assert.False(await outbox.EnqueueAsync(new EmailMessage(one.ToUpperInvariant(), "Aviso en mayúsculas", "t", "<p>t</p>"), fact)); // the address is compared without case
        Assert.True(await outbox.EnqueueAsync(new EmailMessage(two, "Aviso", "t", "<p>t</p>"), fact)); // another address is another person
        Assert.True(await outbox.EnqueueAsync(new EmailMessage(one, "Aviso de otro hecho", "t", "<p>t</p>"), $"otro:{Guid.NewGuid():N}"));
        Assert.True(await outbox.EnqueueAsync(new EmailMessage(one, "Sin clave", "t", "<p>t</p>")));
        Assert.True(await outbox.EnqueueAsync(new EmailMessage(one, "Sin clave", "t", "<p>t</p>"))); // without a key, nothing is promised

        await api.DrainMailAsync();
        Assert.Equal(["Aviso", "Aviso de otro hecho", "Sin clave", "Sin clave"], api.Mail.To(one).Select(m => m.Subject).Order(StringComparer.Ordinal).ToArray());
    }
}
