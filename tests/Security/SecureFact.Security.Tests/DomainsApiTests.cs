using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The domains of the resellers (ADR-051): assigning one, proving it in the DNS, losing it, and what the edge is told.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DomainsApiTests(ApiFixture api)
{
    private sealed record Row(Guid Id, string Name);

    private sealed record Domain(Guid ResellerId, string? Host, string Status, string? TxtName, string? TxtValue, string? CnameTarget, string[] EdgeAddresses, string? Error);

    private sealed record Reseller(Row Row, HttpClient Client);

    private static string NewHost() => $"portal-{Guid.NewGuid():N}"[..20] + ".e2e.test";

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<Reseller> NewResellerAsync(HttpClient admin, bool withBrand = true)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Dominio {Guid.NewGuid():N}"[..18] });
        var row = (await created.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var email = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Admin", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = row.Id })).StatusCode);
        var client = api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword));
        if (withBrand)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/reseller/branding", new { brandName = "Marca de dominio", primaryColor = "#0b5394" })).StatusCode);
        }

        return new Reseller(row, client);
    }

    private static async Task<Domain> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<Domain>(ApiFixture.JsonOptions))!;

    private static Task<HttpResponseMessage> AssignAsync(HttpClient admin, Guid resellerId, string? host) => admin.PutAsJsonAsync($"/api/v1/platform/resellers/{resellerId}/host", new { host });

    private static Task<HttpResponseMessage> VerifyAsAdminAsync(HttpClient admin, Guid resellerId) => admin.PostAsync($"/api/v1/platform/resellers/{resellerId}/domain/verify", null);

    /// <summary>What the reseller does in its DNS: the TXT with the proof and the alias to the edge.</summary>
    private void Publish(Domain domain)
    {
        api.Dns.PublishTxt(domain.TxtName!, domain.TxtValue!);
        api.Dns.PointAt(domain.Host!, FakeDomainNameSystem.EdgeHost);
    }

    private static async Task<int> BrandStatusAsync(ApiFixture api, string host)
    {
        using var anonymous = api.NewClient();
        return (int)(await anonymous.GetAsync($"/api/v1/branding?host={Uri.EscapeDataString(host)}")).StatusCode;
    }

    private async Task<HttpStatusCode> AskEdgeAsync(string domain, string? secret = FakeDomainNameSystem.EdgeSecret)
    {
        using var anonymous = api.NewClient();
        return (await anonymous.GetAsync($"/api/v1/edge/tls-allowed?domain={Uri.EscapeDataString(domain)}{(secret is null ? string.Empty : $"&secret={Uri.EscapeDataString(secret)}")}")).StatusCode;
    }

    [Fact]
    public async Task A_new_domain_is_pending_with_the_two_records_the_reseller_must_create()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();

        var assigned = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, host.ToUpperInvariant() + ":8443"));

        Assert.Equal((host, "Pending"), (assigned.Host, assigned.Status));
        Assert.Equal($"_securefact-challenge.{host}", assigned.TxtName);
        Assert.Matches("^securefact-verification=[0-9a-f]{48}$", assigned.TxtValue);
        Assert.Equal(FakeDomainNameSystem.EdgeHost, assigned.CnameTarget);
        Assert.Equal([FakeDomainNameSystem.EdgeAddress], assigned.EdgeAddresses);

        // The reseller reads the same instructions; the same domain again changes nothing (its proof stays).
        var own = await ReadAsync(await reseller.Client.GetAsync("/api/v1/reseller/domain"));
        Assert.Equal(assigned.TxtValue, own.TxtValue);
        Assert.Equal(assigned.TxtValue, (await ReadAsync(await AssignAsync(admin, reseller.Row.Id, host))).TxtValue);
        // A different domain has a proof of its own.
        Assert.NotEqual(assigned.TxtValue, (await ReadAsync(await AssignAsync(admin, reseller.Row.Id, NewHost()))).TxtValue);
    }

    [Fact]
    public async Task A_domain_is_verified_when_both_records_are_there_and_only_then_it_shows_the_brand_and_may_have_a_certificate()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        var domain = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, host));
        Assert.Equal(204, await BrandStatusAsync(api, host)); // pending: the default look
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(host));

        // Nothing published yet: the answer says what is missing.
        var nothing = await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id));
        Assert.Equal("Pending", nothing.Status);
        Assert.Contains($"_securefact-challenge.{host}", nothing.Error, StringComparison.Ordinal);
        Assert.Contains(domain.TxtValue!, nothing.Error, StringComparison.Ordinal);

        // The proof is there but the name does not lead to the platform.
        await Task.Delay(2100);
        api.Dns.PublishTxt(domain.TxtName!, domain.TxtValue!);
        api.Dns.PointAt(host, "otro-proveedor.example.net");
        var wrongRoute = await ReadAsync(await reseller.Client.PostAsync("/api/v1/reseller/domain/verify", null));
        Assert.Equal("Pending", wrongRoute.Status);
        Assert.Contains(FakeDomainNameSystem.EdgeHost, wrongRoute.Error, StringComparison.Ordinal);
        Assert.Contains("otro-proveedor.example.net", wrongRoute.Error, StringComparison.Ordinal);
        Assert.Equal(204, await BrandStatusAsync(api, host));

        // Both records: verified. The brand shows and the edge may ask for the certificate.
        await Task.Delay(2100);
        api.Dns.PointAt(host, FakeDomainNameSystem.EdgeHost);
        var verified = await ReadAsync(await reseller.Client.PostAsync("/api/v1/reseller/domain/verify", null));
        Assert.Equal(("Verified", null), (verified.Status, verified.Error));
        Assert.Equal(200, await BrandStatusAsync(api, host));
        Assert.Equal(HttpStatusCode.OK, await AskEdgeAsync(host));
        Assert.Equal(HttpStatusCode.OK, await AskEdgeAsync(host.ToUpperInvariant()));
    }

    [Fact]
    public async Task A_name_that_cannot_be_an_alias_is_routed_by_the_addresses_of_the_edge()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        var domain = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, host));
        api.Dns.PublishTxt(domain.TxtName!, domain.TxtValue!);

        api.Dns.ResolveTo(host, "198.51.100.7"); // the address of someone else
        Assert.Equal("Pending", (await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id))).Status);

        await Task.Delay(2100);
        api.Dns.ResolveTo(host, FakeDomainNameSystem.EdgeAddress);
        Assert.Equal("Verified", (await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id))).Status);
    }

    [Fact]
    public async Task A_check_is_not_repeated_within_seconds_and_a_resolver_that_does_not_answer_is_a_failed_check_not_an_error()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var domain = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, NewHost()));
        Publish(domain);

        api.Dns.Down = true;
        try
        {
            var down = await VerifyAsAdminAsync(admin, reseller.Row.Id);
            Assert.Equal(HttpStatusCode.OK, down.StatusCode);
            var read = await ReadAsync(down);
            Assert.Equal("Pending", read.Status);
            Assert.Contains("No se pudo consultar el DNS", read.Error, StringComparison.Ordinal);
        }
        finally
        {
            api.Dns.Down = false;
        }

        var tooSoon = await VerifyAsAdminAsync(admin, reseller.Row.Id);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Equal("SF-DOM-002", await CodeAsync(tooSoon));

        await Task.Delay(2100);
        Assert.Equal("Verified", (await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id))).Status);
    }

    [Fact]
    public async Task A_verified_domain_that_stops_answering_becomes_unreachable_after_several_checks_and_comes_back_by_itself()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        var domain = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, host));
        Publish(domain);
        Assert.Equal("Verified", (await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id))).Status);

        api.Dns.Unpublish(domain.TxtName!);
        var states = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(2100);
            states.Add((await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id))).Status);
        }

        Assert.Equal(["Verified", "Verified", "Unreachable"], states); // a blip of the DNS does not take the portal down; three in a row do
        Assert.Equal(204, await BrandStatusAsync(api, host));
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(host));

        api.Dns.PublishTxt(domain.TxtName!, domain.TxtValue!);
        await Task.Delay(2100);
        Assert.Equal("Verified", (await ReadAsync(await VerifyAsAdminAsync(admin, reseller.Row.Id))).Status);
        Assert.Equal(200, await BrandStatusAsync(api, host));

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/audit?entityType=reseller&take=100", ApiFixture.JsonOptions))!
            .Where(e => e.GetProperty("entityId").GetString() == reseller.Row.Id.ToString("D")).Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("tenancy.reseller.domain_changed", events);
        Assert.Equal(2, events.Count(e => e == "tenancy.reseller.domain_verified"));
        Assert.Single(events, e => e == "tenancy.reseller.domain_unreachable");
    }

    [Fact]
    public async Task Changing_or_clearing_the_domain_starts_again_and_the_platform_names_are_not_for_a_reseller()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var first = NewHost();
        var domain = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, first));
        Publish(domain);
        await VerifyAsAdminAsync(admin, reseller.Row.Id);
        Assert.Equal(200, await BrandStatusAsync(api, first));

        var second = NewHost();
        var changed = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, second));
        Assert.Equal("Pending", changed.Status);
        Assert.Equal(204, await BrandStatusAsync(api, first)); // the old name is not the reseller's any more
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(first));

        var cleared = await ReadAsync(await AssignAsync(admin, reseller.Row.Id, null));
        Assert.Equal(("None", null, null), (cleared.Status, cleared.Host, cleared.TxtValue));
        var noDomain = await VerifyAsAdminAsync(admin, reseller.Row.Id);
        Assert.Equal("SF-DOM-003", await CodeAsync(noDomain));

        foreach (var reserved in new[] { FakeDomainNameSystem.PlatformHost, FakeDomainNameSystem.EdgeHost, FakeDomainNameSystem.EdgeHost.ToUpperInvariant() })
        {
            var refused = await AssignAsync(admin, reseller.Row.Id, reserved);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-DOM-001", await CodeAsync(refused));
        }

        var other = await NewResellerAsync(admin);
        var taken = NewHost();
        await AssignAsync(admin, reseller.Row.Id, taken);
        Assert.Equal("SF-BRAND-003", await CodeAsync(await AssignAsync(admin, other.Row.Id, taken)));
    }

    [Fact]
    public async Task Each_reseller_sees_and_checks_only_its_own_domain_and_only_the_super_admin_assigns()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewResellerAsync(admin);
        var two = await NewResellerAsync(admin);
        await AssignAsync(admin, two.Row.Id, NewHost());
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var tenantId = await api.CreateTenantAsync("Sin dominio SAC");
        var ownerUser = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        using var owner = api.ClientFor(await api.LoginOkAsync(ownerUser.Email, ownerUser.Password));

        Assert.Equal("None", (await ReadAsync(await one.Client.GetAsync("/api/v1/reseller/domain"))).Status); // its own: none; never the one of another
        Assert.Equal(HttpStatusCode.Forbidden, (await one.Client.GetAsync($"/api/v1/platform/resellers/{two.Row.Id}/domain")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await one.Client.PostAsync($"/api/v1/platform/resellers/{two.Row.Id}/domain/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(one.Client, one.Row.Id, NewHost())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/reseller/domain")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/v1/reseller/domain/verify", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync($"/api/v1/platform/resellers/{two.Row.Id}/domain")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(supportClient, one.Row.Id, NewHost())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await VerifyAsAdminAsync(supportClient, two.Row.Id)).StatusCode);
    }

    [Fact]
    public async Task The_edge_is_answered_only_when_it_gives_the_secret_and_the_platforms_own_hosts_always_have_their_certificate()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        Publish(await ReadAsync(await AssignAsync(admin, reseller.Row.Id, host)));
        await VerifyAsAdminAsync(admin, reseller.Row.Id);

        Assert.Equal(HttpStatusCode.OK, await AskEdgeAsync(host));
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(host, secret: null));
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(host, secret: "otra-clave"));
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync("desconocido.e2e.test"));
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(string.Empty));
        Assert.Equal(HttpStatusCode.OK, await AskEdgeAsync(FakeDomainNameSystem.PlatformHost));

        // A reseller that is switched off has no certificate for its portal.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}", new { name = reseller.Row.Name, isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await AskEdgeAsync(host));
    }

    [Fact]
    public async Task The_background_pass_promotes_a_pending_domain_once_its_records_are_there_and_skips_a_reseller_that_is_off()
    {
        using var admin = await api.AdminClientAsync();
        var ready = await NewResellerAsync(admin);
        var off = await NewResellerAsync(admin);
        var waiting = await NewResellerAsync(admin);
        var readyDomain = await ReadAsync(await AssignAsync(admin, ready.Row.Id, NewHost()));
        var offDomain = await ReadAsync(await AssignAsync(admin, off.Row.Id, NewHost()));
        var waitingDomain = await ReadAsync(await AssignAsync(admin, waiting.Row.Id, NewHost()));
        Publish(readyDomain);
        Publish(offDomain);
        await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{off.Row.Id}", new { name = off.Row.Name, isActive = false });

        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("test: the pass of the domain worker");
            Assert.True(await scope.ServiceProvider.GetRequiredService<IDomains>().VerifyDueAsync(CancellationToken.None) >= 2);
        }

        Assert.Equal("Verified", (await ReadAsync(await admin.GetAsync($"/api/v1/platform/resellers/{ready.Row.Id}/domain"))).Status);
        Assert.Equal("Pending", (await ReadAsync(await admin.GetAsync($"/api/v1/platform/resellers/{off.Row.Id}/domain"))).Status); // switched off: not checked
        var stillWaiting = await ReadAsync(await admin.GetAsync($"/api/v1/platform/resellers/{waiting.Row.Id}/domain"));
        Assert.Equal("Pending", stillWaiting.Status); // checked, and it says what it lacks
        Assert.Contains(waitingDomain.TxtName!, stillWaiting.Error, StringComparison.Ordinal);
    }
}
