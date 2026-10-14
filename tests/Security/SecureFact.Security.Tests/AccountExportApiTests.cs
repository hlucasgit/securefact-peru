using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>An account takes all its data with it in one ZIP (ADR-074): every module contributes its tables, the signed files come as they are kept, no secret travels, and another account never appears.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AccountExportApiTests(ApiFixture api)
{
    private const string Path = "/api/v1/account/export";
    // Generated for each run, so there is no password written in the source.
    private static readonly string SolPassword = "Sol-" + Guid.NewGuid().ToString("N")[..16];

    private static int _rucCounter = 60_000_000;

    private static readonly string[] HookEvents = ["document.accepted"];

    private static readonly string[] SecretWords = ["clave", "password", "hash", "mfa_secret", "secreto"];

    private sealed record Account(Guid TenantId, string OwnerEmail, string OwnerPassword, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, string Ruc, string PfxBase64);

    private sealed record AuditRow(string Action, JsonElement? NewValues);

    private sealed record SessionRow(string AccessToken);

    private sealed record Package(Dictionary<string, byte[]> Files)
    {
        public string Text(string name) => Encoding.UTF8.GetString(Files[name]).TrimStart((char)0xFEFF);

        /// <summary>The lines of a table without its header.</summary>
        public string[] Rows(string table) => Text($"datos/{table}.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1..];

        public string All() => string.Join('\n', Files.Where(f => !f.Key.EndsWith(".zip", StringComparison.Ordinal)).Select(f => Encoding.UTF8.GetString(f.Value)));
    }

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateOnly TodayInLima() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime);

    private static string PfxFor(string ruc)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    private async Task<Account> NewAccountAsync(string name)
    {
        api.Sunat.Reset();
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..28]);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = $"Emisora {ruc} SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = SolPassword })).StatusCode);
        var pfx = PfxFor(ruc);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = pfx, password = "pw" })).StatusCode);
        return new Account(tenantId, user.Email, user.Password, owner, company, series, ruc, pfx);
    }

    private static async Task<DocumentDto> IssueAsync(Account account, string buyerName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = account.Invoice.Id,
                issueDate = Iso(TodayInLima()),
                currency = "PEN",
                buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = buyerName },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await account.Owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private async Task<Guid> AcceptAsync(Account account, DocumentDto document)
    {
        Assert.True((await account.Owner.PostAsync($"/api/v1/documents/{document.Id}/electronic", null)).IsSuccessStatusCode);
        var electronic = (await account.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{document.Id}/electronic", ApiFixture.JsonOptions))!;
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(account.Ruc, $"{document.Series}-{document.Number}")));
        var sent = (await (await account.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);
        return electronic.Id;
    }

    private static async Task<Package> TakeAsync(HttpClient client)
    {
        var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^cuenta-[0-9a-f]{32}-\d{8}\.zip$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            await using var stream = await entry.OpenAsync();
            var content = new MemoryStream();
            await stream.CopyToAsync(content);
            files[entry.FullName] = content.ToArray();
        }

        return new Package(files);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task The_owner_takes_every_table_of_the_account_and_the_signed_files_as_they_are_kept()
    {
        var account = await NewAccountAsync("Cuenta completa");
        var accepted = await IssueAsync(account, "Cliente \"Comillas\", SAC");
        var acceptedId = await AcceptAsync(account, accepted);
        var pending = await IssueAsync(account, "=HYPERLINK(\"http://malo.pe\")");
        Assert.Equal(HttpStatusCode.Created, (await account.Owner.PostAsJsonAsync("/api/v1/customers", new { documentTypeCode = "6", documentNumber = "20100070970", name = "=CMD|' /C calc'!A0", email = "cliente@ejemplo.pe" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await account.Owner.PostAsJsonAsync("/api/v1/products", new { internalCode = "P-001", description = "Servicio mensual", kind = "Service", unitCode = "ZZ", unitValue = 100m, igvAffectationCode = "10" })).StatusCode);

        var package = await TakeAsync(account.Owner);

        // The tables of every module that owns data of the account.
        Assert.Contains(account.Ruc, package.Text("datos/empresas.csv"), StringComparison.Ordinal);
        Assert.Contains(account.OwnerEmail, package.Text("datos/usuarios.csv"), StringComparison.Ordinal);
        Assert.Contains(Roles.TenantOwner, package.Text("datos/roles_de_usuarios.csv"), StringComparison.Ordinal);
        Assert.Contains("'=CMD|", package.Text("datos/clientes.csv"), StringComparison.Ordinal); // a customer that wrote a formula is text
        Assert.Contains("P-001", package.Text("datos/productos.csv"), StringComparison.Ordinal);
        Assert.Contains("F001", package.Text("datos/series.csv"), StringComparison.Ordinal);
        Assert.Equal(2, package.Rows("comprobantes").Length);
        Assert.Equal(2, package.Rows("lineas_de_comprobantes").Length);
        Assert.Contains("'=HYPERLINK(", package.Text("datos/comprobantes.csv"), StringComparison.Ordinal);
        Assert.Contains("\"Cliente \"\"Comillas\"\", SAC\"", package.Text("datos/comprobantes.csv"), StringComparison.Ordinal);
        Assert.Contains("118.00", package.Text("datos/comprobantes.csv"), StringComparison.Ordinal);
        Assert.Single(package.Rows("certificados"));
        Assert.Contains("Emisora", package.Text("datos/empresas.csv"), StringComparison.Ordinal);
        Assert.NotEmpty(package.Rows("cuenta"));
        Assert.NotEmpty(package.Rows("auditoria"));
        Assert.Contains("billing.document.created", package.Text("datos/auditoria.csv"), StringComparison.Ordinal);
        foreach (var table in new[] { "cargos", "pagos", "intentos_de_pago_con_tarjeta", "datos_de_facturacion", "webhooks", "claves_de_api", "series_de_guias", "guias" })
        {
            Assert.True(package.Files.ContainsKey($"datos/{table}.csv"), table);
        }

        // The XML as it was signed and sent, and the CDR of the accepted one only.
        var sent = await account.Owner.GetStringAsync($"/api/v1/electronic-documents/{acceptedId}/xml");
        var xmlName = package.Files.Keys.Single(k => k.StartsWith("xml/", StringComparison.Ordinal) && k.Contains($"-{accepted.Series}-{accepted.Number}", StringComparison.Ordinal));
        Assert.Equal(sent, Encoding.UTF8.GetString(package.Files[xmlName]));
        Assert.Single(package.Files.Keys, k => k.StartsWith("cdr/R-", StringComparison.Ordinal));
        Assert.Equal(await account.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{acceptedId}/cdr"), package.Files[package.Files.Keys.Single(k => k.StartsWith("cdr/R-", StringComparison.Ordinal))]);
        Assert.True(package.Files.ContainsKey("LEEME.txt"));
        Assert.Contains("No incluye", package.Text("LEEME.txt"), StringComparison.Ordinal);
        Assert.Single(package.Rows("envios_a_sunat")); // the one that was prepared and sent; the other one is only issued
        Assert.Contains(pending.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), package.Text("datos/comprobantes.csv"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_of_contents_proves_the_package_is_complete()
    {
        var account = await NewAccountAsync("Contenido SAC");
        await AcceptAsync(account, await IssueAsync(account, "Cliente SAC"));

        var package = await TakeAsync(account.Owner);

        var rows = package.Rows("contenido").Select(line => line.Split(',')).ToList();
        foreach (var row in rows.Where(r => r[0].StartsWith("datos/", StringComparison.Ordinal)))
        {
            var content = package.Files[row[0]];
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), row[2]);
            var lines = Encoding.UTF8.GetString(content).TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
            Assert.True(row[1] == lines.ToString(System.Globalization.CultureInfo.InvariantCulture) || row[0].Contains("auditoria", StringComparison.Ordinal) || Encoding.UTF8.GetString(content).Contains('"', StringComparison.Ordinal), row[0]);
        }

        Assert.Equal(1, int.Parse(rows.Single(r => r[0] == "xml/")[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, int.Parse(rows.Single(r => r[0] == "cdr/")[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task No_secret_travels_in_the_package()
    {
        var account = await NewAccountAsync("Sin secretos");
        await AcceptAsync(account, await IssueAsync(account, "Cliente SAC"));
        var key = await account.Owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "Integración", role = Roles.ReadOnly });
        var created = await key.Content.ReadAsStringAsync();
        var secret = JsonDocument.Parse(created).RootElement.GetProperty("secret").GetString()!;
        var hook = await account.Owner.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://ejemplo.pe/hook", events = HookEvents });
        var hookBody = await hook.Content.ReadAsStringAsync();
        var hookSecret = JsonDocument.Parse(hookBody).RootElement.GetProperty("secret").GetString()!;
        Assert.True(hook.IsSuccessStatusCode, hookBody);

        var package = await TakeAsync(account.Owner);
        var everything = package.All();

        Assert.DoesNotContain(account.OwnerPassword, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(SolPassword, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(account.PfxBase64[..60], everything, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(hookSecret, everything, StringComparison.Ordinal);
        var userHeader = package.Text("datos/usuarios.csv").Split("\r\n")[0];
        Assert.All(SecretWords, word => Assert.DoesNotContain(word, userHeader, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("https://ejemplo.pe/hook", package.Text("datos/webhooks.csv"), StringComparison.Ordinal);
        var apiKeys = package.Text("datos/claves_de_api.csv");
        Assert.Contains("Integración", apiKeys, StringComparison.Ordinal);
        Assert.DoesNotContain(secret[(secret.LastIndexOf('_') + 1)..], apiKeys, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_account_never_appears_in_the_package()
    {
        var first = await NewAccountAsync("Primera cuenta");
        await AcceptAsync(first, await IssueAsync(first, "Comprador Exclusivo De La Primera"));
        var second = await NewAccountAsync("Segunda cuenta");

        var package = await TakeAsync(second.Owner);
        var everything = package.All();

        Assert.DoesNotContain(first.Ruc, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(first.OwnerEmail, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Comprador Exclusivo De La Primera", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(first.TenantId.ToString("D"), everything, StringComparison.Ordinal);
        Assert.Contains(second.Ruc, package.Text("datos/empresas.csv"), StringComparison.Ordinal);
        Assert.Empty(package.Rows("comprobantes"));
        Assert.DoesNotContain(package.Files.Keys, k => k.StartsWith("xml/", StringComparison.Ordinal) || k.StartsWith("cdr/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_bigger_than_a_batch_comes_whole_and_without_repeats()
    {
        var account = await NewAccountAsync("Muchos clientes");
        var csv = new StringBuilder("tipo_documento,numero_documento,nombre\n");
        for (var i = 0; i < 1_240; i++)
        {
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"1,{70_000_000 + i:D8},Cliente {i:D4}\n");
        }

        var imported = await account.Owner.PostAsJsonAsync("/api/v1/customers/import", new { csv = csv.ToString(), commit = true });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var package = await TakeAsync(account.Owner);

        var rows = package.Rows("clientes");
        Assert.Equal(1_240, rows.Length);
        Assert.Equal(1_240, rows.Select(r => r.Split(',')[2]).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Only_the_owner_takes_the_data_and_the_platform_and_the_people_of_support_do_not()
    {
        var account = await NewAccountAsync("Quién exporta");
        using var admin = await api.AdminClientAsync();
        foreach (var role in new[] { Roles.TenantAdmin, Roles.BillingAdmin, Roles.Accountant, Roles.Auditor, Roles.ReadOnly, Roles.Developer, Roles.Sales })
        {
            var user = await ApiFixture.CreateUserAsync(admin, role, account.TenantId);
            using var client = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
        }

        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        // The platform holds the permission but is in no account: there is nothing of an account to take.
        var platform = await admin.GetAsync(Path);
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
        Assert.Equal("SF-AUTH-003", await CodeAsync(platform));

        // A person of support, inside the account with its authorization, reads but does not take the whole of it.
        Assert.Equal(HttpStatusCode.Created, (await account.Owner.PostAsJsonAsync("/api/v1/support-access", new { hours = 1, note = "Ayuda" })).StatusCode);
        var staff = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var staffClient = api.ClientFor(await api.LoginOkAsync(staff.Email, staff.Password));
        var entered = await staffClient.PostAsJsonAsync("/api/v1/support-sessions", new { tenantId = account.TenantId, reason = "Revisar la cuenta" });
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        using var support = api.NewClient();
        support.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await entered.Content.ReadFromJsonAsync<SessionRow>(ApiFixture.JsonOptions))!.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task Each_export_is_audited_without_the_data_and_the_person_who_took_it_is_known()
    {
        var account = await NewAccountAsync("Auditoría de la cuenta");

        await TakeAsync(account.Owner);

        var rows = (await account.Owner.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=tenancy.account.exported", ApiFixture.JsonOptions))!;
        var row = Assert.Single(rows);
        Assert.Equal(AuditActions.AccountExported, row.Action);
        Assert.DoesNotContain(account.Ruc, row.NewValues?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_owner_of_the_account_is_told_that_its_data_were_taken_and_by_whom()
    {
        var account = await NewAccountAsync("Aviso de exportación");
        using var admin = await api.AdminClientAsync();
        var second = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, account.TenantId);

        await TakeAsync(account.Owner);
        await TakeAsync(account.Owner);
        await api.DrainMailAsync();

        const string subject = "Se descargaron los datos de su cuenta";
        var first = api.Mail.To(account.OwnerEmail).Where(m => m.Subject == subject).ToList();
        Assert.Equal(2, first.Count); // one for each export: each is a different fact
        Assert.Equal(2, api.Mail.To(second.Email).Count(m => m.Subject == subject));
        Assert.Contains("descargó todos los datos de su cuenta", first[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(account.OwnerPassword, first[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(account.Ruc, first[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_export_is_limited_by_credential_because_it_is_the_heaviest_read()
    {
        var account = await NewAccountAsync("Límite de exportación");
        var previous = Environment.GetEnvironmentVariable("RateLimiting__ExportPermitPerHour");
        Environment.SetEnvironmentVariable("RateLimiting__ExportPermitPerHour", "2");
        try
        {
            await using var limited = new WebApplicationFactory<Program>();
            using var client = limited.CreateClient();
            var login = await api.LoginOkAsync(account.OwnerEmail, account.OwnerPassword);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 3; i++)
            {
                using var response = await client.GetAsync(Path);
                statuses.Add(response.StatusCode);
            }

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RateLimiting__ExportPermitPerHour", previous);
        }
    }
}
