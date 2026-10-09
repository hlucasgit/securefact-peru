using System.Net;
using System.Net.Http.Json;
using SecureFact.Gre.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The guides of import (08), export (09), foreign goods (19) and itinerant issuer (18) through the API, with the catalogues of ports and units that the database holds (ADR-059).</summary>
public sealed partial class GreApiTests
{
    private static object Base(Setup setup, string motive, object recipient, object origin, object? destination, object? related, object? customs, int? packages, object? goods, decimal weight = 100m) => new
    {
        companyId = setup.Company.Id,
        seriesId = setup.Series.Id,
        motiveCode = motive,
        modalityCode = "02",
        transferStartDate = Today(),
        grossWeight = weight,
        weightUnit = "KGM",
        packageCount = packages,
        recipient,
        origin,
        destination,
        vehicle = new { plate = "ABC123", circulationCard = "1234567890" },
        driver = new { documentTypeCode = "1", documentNumber = "12345678", firstNames = "JUAN CARLOS", lastNames = "PEREZ GOMEZ", licenseNumber = "Q12345678" },
        goods = goods ?? Array.Empty<object>(),
        relatedDocuments = related,
        customs,
    };

    private static readonly object Importer = new { documentTypeCode = "6", documentNumber = "20100070970", name = "IMPORTADORA DEMO SAC" };

    private static async Task<GreDto> CreateBodyOkAsync(Setup setup, object body)
    {
        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
    }

    [Fact]
    public async Task An_import_with_its_declaration_the_port_and_the_whole_lot_is_numbered_signed_and_accepted()
    {
        var setup = await NewTenantAsync("gre-import");
        var body = Base(
            setup, "08", Importer, new { ubigeoCode = "070101", address = "Terminal portuario del Callao" }, new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" },
            new[] { new { typeCode = "50", number = "118-2026-10-123456", issuerRuc = (string?)null } },
            new { portCode = "CLL", portType = "1", portName = "Callao", wholeTransfer = true }, 3, null);

        var guide = await CreateBodyOkAsync(setup, body);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{guide.Id}/xml");

        Assert.Equal("08", guide.MotiveCode);
        Assert.Contains("SUNAT_Envio_IndicadorTrasladoTotalDAMoDS", xml, StringComparison.Ordinal);
        Assert.Contains("FirstArrivalPortLocation", xml, StringComparison.Ordinal);
        Assert.Equal(GreState.Accepted, (await (await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null)).Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!.State);
        var pdf = PdfText(await PdfOkAsync(setup, guide.Id));
        Assert.Contains("Aduanas", pdf, StringComparison.Ordinal);
        Assert.Contains("CLL - Callao", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_lists_the_goods_of_the_declaration_with_their_series_in_the_units_of_customs()
    {
        var setup = await NewTenantAsync("gre-export");
        var declaration = "118-2026-40-654321";
        var body = Base(
            setup, "09", Importer, new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" }, new { ubigeoCode = "070101", address = "Terminal portuario del Callao" },
            new[] { new { typeCode = "50", number = declaration, issuerRuc = (string?)null } },
            new { portCode = "CLL", portType = "1", portName = "Callao", netWeight = 1450.5m, weightNote = "El peso bruto incluye los sacos" }, 12,
            new[] { new { description = "Café en grano", unitCode = "2U", quantity = 100m, customs = new { declarationNumber = declaration, declarationSeries = "1" } } });

        var guide = await CreateBodyOkAsync(setup, body);

        Assert.Contains("7021", await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{guide.Id}/xml"), StringComparison.Ordinal);

        var wrongUnit = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Base(
            setup, "09", Importer, new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" }, new { ubigeoCode = "070101", address = "Terminal portuario del Callao" },
            new[] { new { typeCode = "50", number = declaration, issuerRuc = (string?)null } },
            new { portCode = "CLL", portType = "1", portName = "Callao", netWeight = 1450.5m, weightNote = "nota de peso" }, 12,
            new[] { new { description = "Café en grano", unitCode = "ZZZ", quantity = 100m, customs = new { declarationNumber = declaration, declarationSeries = "1" } } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongUnit.StatusCode);
        Assert.Contains("3446", await wrongUnit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Foreign_goods_with_a_delivery_order_or_a_manifest_and_an_itinerant_issuer_are_issued()
    {
        var setup = await NewTenantAsync("gre-foreign");
        var destination = new { ubigeoCode = "150101", address = "Depósito autorizado", establishmentRuc = "20100070970", establishmentCode = "0001" };

        var order = await CreateBodyOkAsync(setup, Base(
            setup, "19", Importer, new { ubigeoCode = "070101", address = "Terminal portuario del Callao" }, destination,
            new[] { new { typeCode = "92", number = "12345678901234", issuerRuc = (string?)"20100070970" } }, new { portCode = "CLL", portType = "1", portName = "Callao" }, null, null, weight: 0m));
        var manifest = await CreateBodyOkAsync(setup, Base(
            setup, "19", Importer, new { ubigeoCode = "070101", address = "Terminal portuario del Callao" }, destination,
            new[] { new { typeCode = "91", number = "01-118-1-2026-45", issuerRuc = (string?)null } }, new { portCode = "CLL", portType = "1", portName = "Callao" }, 4,
            new[] { new { description = "Repuestos", unitCode = "U", quantity = 40m, customs = new { transportDocument = "HBL-998", transportDetail = "1" } } }));
        var itinerant = await CreateBodyOkAsync(setup, Base(
            setup, "18", new { documentTypeCode = "6", documentNumber = setup.Company.Ruc, name = "Emisora SAC" }, new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" }, null, null, null, null,
            new[] { new { description = "Mercadería de reparto", unitCode = "NIU", quantity = 20m } }));

        Assert.Equal(["T001-1", "T001-2", "T001-3"], [order.Name, manifest.Name, itinerant.Name]);
        Assert.DoesNotContain("DeliveryAddress", await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{itinerant.Id}/xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("GrossWeightMeasure", await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{order.Id}/xml"), StringComparison.Ordinal);
        Assert.Contains("No se informa (emisor itinerante)", PdfText(await PdfOkAsync(setup, itinerant.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_guide_with_customs_data_that_breaks_the_rules_is_refused_with_the_codes()
    {
        var setup = await NewTenantAsync("gre-customs-invalid");
        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Base(
            setup, "19", Importer, new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" }, new { ubigeoCode = "150101", address = "Depósito" },
            new[] { new { typeCode = "92", number = "123", issuerRuc = (string?)"20100070970" }, new { typeCode = "91", number = "01-118-1-2026-45", issuerRuc = (string?)null } }, null, null, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.Contains("3613", detail, StringComparison.Ordinal);
        Assert.Contains("3483", detail, StringComparison.Ordinal);
        Assert.Contains("3369", detail, StringComparison.Ordinal);
    }
}
