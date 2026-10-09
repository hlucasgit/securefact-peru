using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SecureFact.Gre.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The guide of the carrier (type 31) through the API: series «V…», numbering, XML, submission and isolation (ADR-057).</summary>
public sealed partial class GreApiTests
{
    private const string ShipperRuc = "20100070970";

    private static object CarrierGuide(Setup setup, Guid? seriesId = null) => new
    {
        companyId = setup.Company.Id,
        seriesId = seriesId ?? setup.CarrierSeries.Id,
        transferStartDate = Today(),
        grossWeight = 1500.5m,
        weightUnit = "KGM",
        packageCount = 10,
        mtcRegistration = "MTC123456",
        sender = new { documentTypeCode = "6", documentNumber = ShipperRuc, name = "REMITENTE DEMO SAC" },
        recipient = new { documentTypeCode = "6", documentNumber = "20100066603", name = "CLIENTE DEMO SAC" },
        origin = new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" },
        destination = new { ubigeoCode = "040101", address = "Calle Mercaderes 45, Arequipa" },
        vehicle = new { plate = "ABC123", circulationCard = "1234567890" },
        driver = new { documentTypeCode = "1", documentNumber = "12345678", firstNames = "JUAN CARLOS", lastNames = "PEREZ GOMEZ", licenseNumber = "Q12345678" },
        goods = new[] { new { description = "Cajas de repuestos", unitCode = "NIU", quantity = 10m, code = "REP-01" } },
        relatedDocuments = new[] { new { typeCode = "01", number = "F001-123", issuerRuc = ShipperRuc } },
    };

    private static async Task<GreDto> CreateCarrierOkAsync(Setup setup, object? body = null)
    {
        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides/carrier", body ?? CarrierGuide(setup));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
    }

    [Fact]
    public async Task A_guide_of_the_carrier_is_numbered_in_its_own_series_signed_and_prepared()
    {
        var setup = await NewTenantAsync("gre-carrier-create");

        var first = await CreateCarrierOkAsync(setup);
        var second = await CreateCarrierOkAsync(setup);
        var sender = await CreateOkAsync(setup);

        Assert.Equal(GreState.Prepared, first.State);
        Assert.Equal("31", first.DocumentTypeCode);
        Assert.Equal("V001-1", first.Name);
        Assert.Equal("V001-2", second.Name);
        Assert.Equal("T001-1", sender.Name);
        Assert.Null(first.MotiveCode);
        Assert.Null(first.ModalityCode);
        Assert.Equal("09", sender.DocumentTypeCode);

        var xml = await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{first.Id}/xml");
        Assert.Contains("DespatchAdvice", xml, StringComparison.Ordinal);
        Assert.Contains(">31<", xml, StringComparison.Ordinal);
        Assert.Contains("V001-1", xml, StringComparison.Ordinal);
        Assert.Contains("SignatureValue", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_series_of_the_other_type_is_refused_in_both_directions()
    {
        var setup = await NewTenantAsync("gre-carrier-series");

        var carrierOnSenderSeries = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides/carrier", CarrierGuide(setup, setup.Series.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, carrierOnSenderSeries.StatusCode);
        Assert.Equal("SF-GRE-005", await ProblemCodeAsync(carrierOnSenderSeries));

        var senderOnCarrierSeries = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Guide(setup, seriesId: setup.CarrierSeries.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, senderOnCarrierSeries.StatusCode);
        Assert.Equal("SF-GRE-005", await ProblemCodeAsync(senderOnCarrierSeries));
    }

    [Fact]
    public async Task A_guide_of_the_carrier_that_breaks_the_rules_is_refused_with_the_reasons()
    {
        var setup = await NewTenantAsync("gre-carrier-invalid");

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides/carrier", new
        {
            companyId = setup.Company.Id,
            seriesId = setup.CarrierSeries.Id,
            transferStartDate = Today(),
            grossWeight = 0m,
            weightUnit = "KGM",
            sender = new { documentTypeCode = "6", documentNumber = setup.Company.Ruc, name = "YO MISMO" },
            recipient = new { documentTypeCode = "6", documentNumber = ShipperRuc, name = "CLIENTE" },
            origin = new { ubigeoCode = "150101", address = "Av. Argentina 123" },
            destination = new { ubigeoCode = "040101", address = "Calle Mercaderes 45" },
            vehicle = new { plate = "ABC123" },
            driver = new { documentTypeCode = "1", documentNumber = "12345678", firstNames = "JUAN", lastNames = "PEREZ", licenseNumber = "Q12345678" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-GRE-001", await ProblemCodeAsync(response));
        var detail = await response.Content.ReadAsStringAsync();
        Assert.Contains("2560", detail, StringComparison.Ordinal);
        Assert.Contains("4399", detail, StringComparison.Ordinal);
        Assert.Contains("3435", detail, StringComparison.Ordinal);

        var missing = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides/carrier", new { companyId = setup.Company.Id, seriesId = setup.CarrierSeries.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
    }

    [Fact]
    public async Task A_guide_of_the_carrier_is_sent_with_its_own_type_and_file_name_and_accepted()
    {
        var setup = await NewTenantAsync("gre-carrier-submit");
        var guide = await CreateCarrierOkAsync(setup);

        var response = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(GreState.Accepted, accepted.State);
        var submission = Assert.Single(api.Gre.Submissions);
        Assert.Equal("31", submission.DocumentTypeCode);
        Assert.Equal($"{setup.Company.Ruc}-31-V001-1", submission.FileBaseName);
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.GetAsync($"/api/v1/gre/guides/{guide.Id}/cdr")).StatusCode);
    }

    [Fact]
    public async Task A_guide_of_the_carrier_that_relies_on_the_guide_of_the_sender_needs_no_goods()
    {
        var setup = await NewTenantAsync("gre-carrier-related");
        var body = new
        {
            companyId = setup.Company.Id,
            seriesId = setup.CarrierSeries.Id,
            transferStartDate = Today(),
            grossWeight = 800m,
            weightUnit = "KGM",
            sender = new { documentTypeCode = "6", documentNumber = ShipperRuc, name = "REMITENTE DEMO SAC" },
            recipient = new { documentTypeCode = "6", documentNumber = "20100066603", name = "CLIENTE DEMO SAC" },
            origin = new { ubigeoCode = "150101", address = "" },
            destination = new { ubigeoCode = "040101", address = "" },
            vehicle = new { plate = "ABC123", circulationCard = "1234567890" },
            driver = new { documentTypeCode = "1", documentNumber = "12345678", firstNames = "JUAN", lastNames = "PEREZ", licenseNumber = "Q12345678" },
            relatedDocuments = new[] { new { typeCode = "09", number = "T001-45", issuerRuc = ShipperRuc } },
        };

        var guide = await CreateCarrierOkAsync(setup, body);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{guide.Id}/xml");

        Assert.Contains("T001-45", xml, StringComparison.Ordinal);
        Assert.Equal(GreState.Accepted, (await (await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null)).Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!.State);
    }

    [Fact]
    public async Task One_tenant_never_sees_the_guide_of_the_carrier_of_another_and_the_type_never_changes()
    {
        var one = await NewTenantAsync("gre-carrier-iso-one");
        var other = await NewTenantAsync("gre-carrier-iso-other");
        var guide = await CreateCarrierOkAsync(one);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"/api/v1/gre/guides/{guide.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null)).StatusCode);

        var guideType = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.guide SET document_type_code = '09' WHERE id = '{guide.Id}'"));
        var seriesType = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.series SET document_type_code = '09' WHERE id = '{one.CarrierSeries.Id}'"));
        var prefix = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"INSERT INTO gre.series (id, tenant_id, company_id, document_type_code, code, last_number, is_active, created_at, updated_at) VALUES (gen_random_uuid(), '{one.TenantId}', '{one.Company.Id}', '31', 'T999', 0, true, now(), now())"));

        Assert.Equal("42501", guideType.SqlState);
        Assert.Equal("42501", seriesType.SqlState);
        Assert.Equal("23514", prefix.SqlState);
    }
}
