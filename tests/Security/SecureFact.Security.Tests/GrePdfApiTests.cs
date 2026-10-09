using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using SecureFact.CpeEngine;
using SecureFact.Gre.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The printed representation of the guides through the API (ADR-058).</summary>
public sealed partial class GreApiTests
{
    private const string QrAddress = "https://e-factura.sunat.gob.pe/v1/contribuyente/gre/comprobantes/descargaqr?hashqr=PRUEBA";

    private static string PdfText(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var lines = new List<string>();
        foreach (Match stream in Regex.Matches(text, @"stream\n(?<body>.*?)\nendstream", RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
        {
            using var zlib = new ZLibStream(new MemoryStream(Encoding.Latin1.GetBytes(stream.Groups["body"].Value)), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            var content = reader.ReadToEnd();
            lines.AddRange(Regex.Matches(content, @"\((?<t>(?:\\.|[^\\)])*)\) Tj", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => m.Groups["t"].Value));
        }

        return string.Join(' ', lines);
    }

    private static async Task<byte[]> PdfOkAsync(Setup setup, Guid id)
    {
        var response = await setup.Owner.GetAsync($"/api/v1/gre/guides/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        return bytes;
    }

    /// <summary>A CDR as SUNAT would answer, with the address of the QR in a reference. The element that carries it in the real CDR is not documented, so this one is only a stand-in (a note would make the parser read it as an observation).</summary>
    private static byte[] CdrWithQr(Setup setup, string reference)
    {
        var xml =
            "<ar:ApplicationResponse xmlns:ar=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" xmlns:cac=\"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2\" " +
            "xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\" xmlns:ext=\"urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2\">" +
            $"<ext:UBLExtensions><ext:UBLExtension><ext:ExtensionContent/></ext:UBLExtension></ext:UBLExtensions><cbc:UBLVersionID>2.0</cbc:UBLVersionID><cbc:CustomizationID>1.0</cbc:CustomizationID><cbc:ID>1</cbc:ID><cbc:IssueDate>2026-10-08</cbc:IssueDate><cbc:IssueTime>10:00:00</cbc:IssueTime><cbc:ResponseDate>2026-10-08</cbc:ResponseDate><cbc:ResponseTime>10:00:00</cbc:ResponseTime>" +
            "<cac:SenderParty><cac:PartyIdentification><cbc:ID>20131312955</cbc:ID></cac:PartyIdentification></cac:SenderParty>" +
            $"<cac:ReceiverParty><cac:PartyIdentification><cbc:ID>{setup.Company.Ruc}</cbc:ID></cac:PartyIdentification></cac:ReceiverParty>" +
            $"<cac:DocumentResponse><cac:Response><cbc:ReferenceID>{reference}</cbc:ReferenceID><cbc:ResponseCode>0</cbc:ResponseCode><cbc:Description>La Guia ha sido aceptada</cbc:Description></cac:Response><cac:DocumentReference><cbc:ID>{QrAddress}</cbc:ID></cac:DocumentReference></cac:DocumentResponse>" +
            "</ar:ApplicationResponse>";
        return new ZipCpePackager().Zip($"R-{reference}", xml).Value;
    }

    [Fact]
    public async Task The_printed_representation_of_a_prepared_guide_has_the_mark_and_no_qr()
    {
        var setup = await NewTenantAsync("gre-pdf-prepared");
        var guide = await CreateOkAsync(setup);

        var text = PdfText(await PdfOkAsync(setup, guide.Id));

        Assert.Contains("SIN VALIDEZ", text, StringComparison.Ordinal);
        Assert.Contains("T001-1", text, StringComparison.Ordinal);
        Assert.Contains($"RUC {setup.Company.Ruc}", text, StringComparison.Ordinal);
        Assert.Contains("Emisora SAC", text, StringComparison.Ordinal);
        Assert.Contains("01 - ", text, StringComparison.Ordinal);
        Assert.Contains("CLIENTE DEMO SAC", text, StringComparison.Ordinal);
        Assert.Contains("Placa ABC123", text, StringComparison.Ordinal);
        Assert.Contains("aún no enviada a SUNAT", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_printed_representation_of_an_accepted_guide_has_no_mark_and_shows_the_answer_of_SUNAT()
    {
        var setup = await NewTenantAsync("gre-pdf-accepted");
        var guide = await CreateOkAsync(setup);
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null)).StatusCode);

        var text = PdfText(await PdfOkAsync(setup, guide.Id));

        Assert.DoesNotContain("SIN VALIDEZ", text, StringComparison.Ordinal);
        Assert.Contains("Guía aceptada por SUNAT", text, StringComparison.Ordinal);
        Assert.Contains("ha sido aceptada", text, StringComparison.Ordinal);
        Assert.Contains("no lleva código QR", text.Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_CDR_carries_an_address_of_SUNAT_the_qr_is_drawn()
    {
        var setup = await NewTenantAsync("gre-pdf-qr");
        var guide = await MakePendingAsync(setup, await CreateOkAsync(setup));
        api.Gre.EnqueueQuery(new GreTicketOutcome(GreTicketStatus.Done, CdrWithQr(setup, "T001-1"), null, null));
        Assert.Equal(GreState.Accepted, (await RefreshAsync(setup, guide.Id)).State);

        var pdf = await PdfOkAsync(setup, guide.Id);

        var text = PdfText(pdf);
        Assert.Contains("Código QR entregado por SUNAT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no lleva código QR", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_printed_representation_of_a_guide_of_the_carrier_names_the_carrier_document()
    {
        var setup = await NewTenantAsync("gre-pdf-carrier");
        var guide = await CreateCarrierOkAsync(setup);

        var text = PdfText(await PdfOkAsync(setup, guide.Id));

        Assert.Contains("V001-1", text, StringComparison.Ordinal);
        Assert.Contains("TRANSPORTISTA", text, StringComparison.Ordinal);
        Assert.Contains("REMITENTE DEMO SAC", text, StringComparison.Ordinal);
        Assert.Contains("Registro MTC", text, StringComparison.Ordinal);
        Assert.Contains("Paga el flete", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Motivo del traslado", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_tenant_never_prints_the_guide_of_another_and_an_unknown_guide_is_not_found()
    {
        var one = await NewTenantAsync("gre-pdf-iso-one");
        var other = await NewTenantAsync("gre-pdf-iso-other");
        var guide = await CreateOkAsync(one);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"/api/v1/gre/guides/{guide.Id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await one.Owner.GetAsync($"/api/v1/gre/guides/{Guid.NewGuid()}/pdf")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/gre/guides/{guide.Id}/pdf")).StatusCode);
    }
}
