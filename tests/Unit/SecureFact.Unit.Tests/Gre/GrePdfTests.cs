using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Printing;

namespace SecureFact.Unit.Tests.Gre;

/// <summary>The printed representation of the guides (ADR-058): what it shows, the mark of a guide that SUNAT has not accepted, and the QR that SUNAT gives.</summary>
public class GrePdfTests
{
    private const string QrUrl = "https://e-factura.sunat.gob.pe/v1/contribuyente/gre/comprobantes/descargaqr?hashqr=ABC123";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Names = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["6"] = new Dictionary<string, string> { ["6"] = "RUC", ["1"] = "DNI" },
        ["18"] = new Dictionary<string, string> { ["01"] = "Transporte público", ["02"] = "Transporte privado" },
        ["20"] = new Dictionary<string, string> { ["01"] = "Venta", ["13"] = "Otros" },
        ["61"] = new Dictionary<string, string> { ["01"] = "Factura", ["09"] = "Guía de remisión remitente" },
    };

    private static GrePrintModel Sender(GreState state = GreState.Accepted, string? qr = QrUrl, CreateGreRequest? request = null) => new(
        GreSamples.SenderRuc, "EMISORA DEMO SAC", "Av. Larco 123, Miraflores", "09", "T001", 7, GreSamples.Today, "10:30:05", state,
        state is GreState.Accepted or GreState.AcceptedWithObservations ? 0 : null, state is GreState.Accepted ? "La Guia de remision T001-7, ha sido aceptada" : null,
        state is GreState.AcceptedWithObservations ? [new GreObservation("4030", "Observación de prueba")] : [], state is GreState.Prepared ? null : DateTimeOffset.Parse("2026-10-08T15:31:00Z", System.Globalization.CultureInfo.InvariantCulture),
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", qr, Names, request ?? GreSamples.PrivateSale(), null);

    private static GrePrintModel Carrier(CreateGreCarrierRequest request, GreState state = GreState.Accepted, string? qr = QrUrl) => new(
        GreSamples.CarrierRuc, "TRANSPORTES DEMO SAC", "Jr. Cusco 456, Lima", "31", "V001", 7, GreSamples.Today, "10:30:05", state, state is GreState.Accepted ? 0 : null, null, [],
        null, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", qr, Names, null, request);

    private static List<string> PageContents(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var contents = new List<string>();
        foreach (Match stream in Regex.Matches(text, @"stream\n(?<body>.*?)\nendstream", RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
        {
            var bytes = Encoding.Latin1.GetBytes(stream.Groups["body"].Value);
            using var zlib = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            contents.Add(reader.ReadToEnd());
        }

        return contents;
    }

    /// <summary>Every string the PDF shows, in order, with the escapes of the literals undone.</summary>
    private static string Text(byte[] pdf) =>
        string.Join(' ', PageContents(pdf).SelectMany(page => Regex.Matches(page, @"\((?<t>(?:\\.|[^\\)])*)\) Tj", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => Regex.Replace(m.Groups["t"].Value, @"\\(.)", "$1", RegexOptions.None, TimeSpan.FromSeconds(1)))));

    private static int Squares(byte[] pdf) => PageContents(pdf).Sum(page => Regex.Count(page, @" re f ", RegexOptions.None, TimeSpan.FromSeconds(5)));

    [Fact]
    public void An_accepted_guide_of_the_sender_shows_the_data_of_the_guide_and_the_qr_of_SUNAT()
    {
        var pdf = GrePdfRenderer.Render(Sender());
        var text = Text(pdf);

        Assert.Contains("GUÍA DE REMISIÓN ELECTRÓNICA - REMITENTE", text, StringComparison.Ordinal);
        Assert.Contains($"RUC {GreSamples.SenderRuc}", text, StringComparison.Ordinal);
        Assert.Contains("EMISORA DEMO SAC", text, StringComparison.Ordinal);
        Assert.Contains("T001-7", text, StringComparison.Ordinal);
        Assert.Contains("08/10/2026", text, StringComparison.Ordinal);
        Assert.Contains("10:30:05", text, StringComparison.Ordinal);
        Assert.Contains("01 - Venta", text, StringComparison.Ordinal);
        Assert.Contains("02 - Transporte privado", text, StringComparison.Ordinal);
        Assert.Contains("CLIENTE DEMO SAC", text, StringComparison.Ordinal);
        Assert.Contains("Placa ABC123", text, StringComparison.Ordinal);
        Assert.Contains("JUAN CARLOS PEREZ GOMEZ", text, StringComparison.Ordinal);
        Assert.Contains("Caja de repuestos", text, StringComparison.Ordinal);
        Assert.Contains("Factura", text, StringComparison.Ordinal);
        Assert.Contains("F001-123", text, StringComparison.Ordinal);
        Assert.Contains("Guía aceptada por SUNAT", text, StringComparison.Ordinal);
        Assert.Contains("ha sido aceptada (código 0)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SIN VALIDEZ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no lleva código QR", text, StringComparison.Ordinal);
        Assert.True(Squares(pdf) > 80, "the QR is drawn as squares");
    }

    [Fact]
    public void Without_the_qr_of_SUNAT_the_guide_is_identified_by_the_ruc_the_series_and_the_number()
    {
        var pdf = GrePdfRenderer.Render(Sender(qr: null));
        var text = Text(pdf);

        Assert.Contains("Esta representación no lleva código QR", text.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("RUC del remitente, serie y número de la guía", text, StringComparison.Ordinal);
        Assert.True(Squares(pdf) < 10);
    }

    [Theory]
    [InlineData(GreState.Prepared, "Guía preparada")]
    [InlineData(GreState.Pending, "a la espera de su constancia")]
    [InlineData(GreState.Rejected, "rechazada por SUNAT")]
    [InlineData(GreState.Failed, "no procesada por SUNAT")]
    public void A_guide_that_SUNAT_has_not_accepted_carries_the_mark_and_says_so(GreState state, string expected)
    {
        var text = Text(GrePdfRenderer.Render(Sender(state, qr: null))).Replace("\n", " ", StringComparison.Ordinal);

        Assert.Contains("SIN VALIDEZ", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_observed_guide_lists_the_observations_of_SUNAT()
    {
        var text = Text(GrePdfRenderer.Render(Sender(GreState.AcceptedWithObservations)));

        Assert.Contains("aceptada por SUNAT con observaciones", text, StringComparison.Ordinal);
        Assert.Contains("Observación 4030: Observación de prueba", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SIN VALIDEZ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guide_in_public_transport_shows_the_carrier_and_the_handover_day()
    {
        var text = Text(GrePdfRenderer.Render(Sender(request: GreSamples.PublicSale())));

        Assert.Contains("Entrega al transportista", text, StringComparison.Ordinal);
        Assert.Contains($"RUC {GreSamples.CarrierRuc} - TRANSPORTES RAPIDOS SAC", text, StringComparison.Ordinal);
        Assert.Contains("MTC123456", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Conductor principal", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guide_of_the_carrier_shows_its_denomination_the_vehicles_the_drivers_and_who_pays()
    {
        var request = GreCarrierTests.WithGoods() with
        {
            Subcontracted = true,
            Subcontractor = new GrePartyInput("6", GreSamples.CustomerRuc, "SUBCONTRATADOR SAC"),
            FreightPayer = GreFreightPayer.ThirdParty,
            ThirdPartyPayer = new GrePartyInput("6", GreSamples.SenderRuc, "PAGADOR SAC"),
            SecondaryDrivers = [new GreDriverInput("1", "87654321", "ANA", "RAMOS", "B87654321")],
            ReturnEmptyVehicle = true,
        };
        var text = Text(GrePdfRenderer.Render(Carrier(request)));

        Assert.Contains("GUÍA DE REMISIÓN ELECTRÓNICA - TRANSPORTISTA", text.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("V001-7", text, StringComparison.Ordinal);
        Assert.Contains("Registro MTC", text, StringComparison.Ordinal);
        Assert.Contains("REMITENTE DEMO SAC", text, StringComparison.Ordinal);
        Assert.Contains("Placa XYZ987", text, StringComparison.Ordinal);
        Assert.Contains("Conductor secundario 1", text, StringComparison.Ordinal);
        Assert.Contains($"RUC {GreSamples.CustomerRuc} - SUBCONTRATADOR SAC", text, StringComparison.Ordinal);
        Assert.Contains("Un tercero: " + GreSamples.SenderRuc + " - PAGADOR SAC", text, StringComparison.Ordinal);
        Assert.Contains("Retorno del vehículo vacío", text, StringComparison.Ordinal);
        Assert.Contains("Cajas de repuestos", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Motivo del traslado", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guide_of_the_carrier_that_relies_on_the_guide_of_the_sender_says_where_the_goods_are()
    {
        var text = Text(GrePdfRenderer.Render(Carrier(GreCarrierTests.WithSenderGuide(), GreState.Prepared, qr: null)));

        Assert.Contains("Según la guía de remisión remitente T001-45", text, StringComparison.Ordinal);
        Assert.Contains("Paga el flete", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bienes a trasladar", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_goods_continue_on_new_pages_with_the_header_and_the_mark()
    {
        var request = GreSamples.PrivateSale() with { Goods = Enumerable.Range(1, 120).Select(i => new GreGoodInput($"Bien número {i} con una descripción larga para ocupar espacio en la tabla", "NIU", i)).ToList() };
        var pdf = GrePdfRenderer.Render(Sender(GreState.Prepared, null, request));

        var pages = PageContents(pdf);
        Assert.True(pages.Count >= 3);
        Assert.All(pages, page => Assert.Contains("(SIN VALIDEZ) Tj", page, StringComparison.Ordinal));
        Assert.All(pages, page => Assert.Contains("(T001-7) Tj", page, StringComparison.Ordinal));
        Assert.Contains("Continuación de la guía T001-7", Text(pdf), StringComparison.Ordinal);
        Assert.Contains("Bien número 120", Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void The_pdf_is_valid_and_the_same_input_gives_the_same_bytes()
    {
        var first = GrePdfRenderer.Render(Sender());
        var second = GrePdfRenderer.Render(Sender());
        var text = Encoding.Latin1.GetString(first);

        Assert.Equal(first, second);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        var startxref = long.Parse(Regex.Match(text, @"startxref\n(\d+)\n").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring((int)startxref, 4));
    }


    // ---------- the address of the QR ----------

    private static string Cdr(string body) =>
        $"<ar:ApplicationResponse xmlns:ar=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">{body}</ar:ApplicationResponse>";

    [Fact]
    public void The_qr_address_is_the_https_address_of_SUNAT_wherever_the_CDR_puts_it()
    {
        Assert.Equal(QrUrl, GreQrUrl.Find(Cdr($"<cbc:ID>1</cbc:ID><cbc:Note>  {QrUrl}  </cbc:Note>")));
        Assert.Equal("https://sunat.gob.pe/qr?x=1", GreQrUrl.Find(Cdr("<cbc:ResponseCode>0</cbc:ResponseCode><cbc:Description>https://sunat.gob.pe/qr?x=1</cbc:Description>")));
    }

    [Theory]
    [InlineData("<cbc:Note>http://e-factura.sunat.gob.pe/qr</cbc:Note>")]
    [InlineData("<cbc:Note>https://example.com/sunat.gob.pe</cbc:Note>")]
    [InlineData("<cbc:Note>https://evilsunat.gob.pe/qr</cbc:Note>")]
    [InlineData("<cbc:Note>La guia fue aceptada https://e-factura.sunat.gob.pe/qr</cbc:Note>")]
    [InlineData("<cbc:Note>urn:pe:gob:sunat:cpe</cbc:Note>")]
    [InlineData("<cbc:Note a=\"https://e-factura.sunat.gob.pe/qr\">sin texto</cbc:Note>")]
    [InlineData("")]
    public void Anything_else_is_not_a_qr_address(string body)
    {
        Assert.Null(GreQrUrl.Find(Cdr(body)));
    }

    [Fact]
    public void A_cdr_that_is_not_xml_or_is_empty_has_no_address()
    {
        Assert.Null(GreQrUrl.Find("no es xml"));
        Assert.Null(GreQrUrl.Find(string.Empty));
        Assert.Null(GreQrUrl.Find("<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><a>&x;</a>"));
    }
}
