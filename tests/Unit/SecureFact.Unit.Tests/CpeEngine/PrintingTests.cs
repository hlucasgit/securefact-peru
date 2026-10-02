using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using QRCoder;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Printing;
using SecureFact.SharedKernel;

namespace SecureFact.Unit.Tests.CpeEngine;

public class AmountInWordsTests
{
    [Theory]
    [InlineData(0, "SON: CERO CON 00/100 SOLES")]
    [InlineData(1, "SON: UNO CON 00/100 SOLES")]
    [InlineData(21, "SON: VEINTIUNO CON 00/100 SOLES")]
    [InlineData(100, "SON: CIEN CON 00/100 SOLES")]
    [InlineData(101, "SON: CIENTO UNO CON 00/100 SOLES")]
    [InlineData(118, "SON: CIENTO DIECIOCHO CON 00/100 SOLES")]
    [InlineData(236, "SON: DOSCIENTOS TREINTA Y SEIS CON 00/100 SOLES")]
    [InlineData(1000, "SON: MIL CON 00/100 SOLES")]
    [InlineData(1001, "SON: MIL UNO CON 00/100 SOLES")]
    [InlineData(21000, "SON: VEINTIÚN MIL CON 00/100 SOLES")]
    [InlineData(100000, "SON: CIEN MIL CON 00/100 SOLES")]
    [InlineData(1000000, "SON: UN MILLÓN CON 00/100 SOLES")]
    [InlineData(1000001, "SON: UN MILLÓN UNO CON 00/100 SOLES")]
    [InlineData(21000000, "SON: VEINTIÚN MILLONES CON 00/100 SOLES")]
    public void Whole_amounts_are_spelled_out(decimal amount, string expected) =>
        Assert.Equal(expected, AmountInWords.Describe(amount, "PEN"));

    [Fact]
    public void Cents_are_printed_as_a_fraction_of_100_and_rounded_like_money()
    {
        Assert.Equal("SON: DOS MILLONES TRESCIENTOS CUARENTA Y CINCO MIL SEISCIENTOS SETENTA Y OCHO CON 90/100 SOLES", AmountInWords.Describe(2_345_678.90m, "PEN"));
        Assert.Equal("SON: CINCO CON 05/100 SOLES", AmountInWords.Describe(5.05m, "PEN"));
        Assert.Equal("SON: UNO CON 00/100 SOLES", AmountInWords.Describe(0.995m, "PEN"));
    }

    [Theory]
    [InlineData("USD", "DÓLARES AMERICANOS")]
    [InlineData("EUR", "EUROS")]
    [InlineData("XYZ", "XYZ")]
    public void The_currency_is_named(string currency, string name) =>
        Assert.EndsWith(" " + name, AmountInWords.Describe(10m, currency), StringComparison.Ordinal);

    [Fact]
    public void Out_of_range_amounts_are_refused_and_the_maximum_works()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmountInWords.Describe(-1m, "PEN"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmountInWords.Describe(AmountInWords.Max + 1m, "PEN"));
        Assert.StartsWith("SON: NOVECIENTOS NOVENTA Y NUEVE MIL", AmountInWords.Describe(AmountInWords.Max, "PEN"), StringComparison.Ordinal);
    }
}

public class PrintedRepresentationTests
{
    private readonly PdfPrintedRepresentationRenderer _renderer = new();

    private static PrintedDocument Document(string type = "01", int lines = 2, string description = "Servicio de consultoría") => new(
        type, type == "01" ? "F001" : "B001", 123, new DateOnly(2026, 9, 30), "PEN",
        "EMISORA DEMO S.A.C.", "Emisora Demo", "20100066603", "AV. LARCO 123 - MIRAFLORES - LIMA - LIMA",
        type == "01" ? "Registro Unico de Contributentes" : "Documento Nacional de Identidad",
        type == "01" ? "20100070970" : "12345678",
        type == "01" ? "CLIENTE DEMO SAC" : "JUAN PEREZ",
        "CALLE LOS OLIVOS 456",
        Enumerable.Range(1, lines).Select(i => new PrintedLine(i % 2 == 0 ? "NIU" : "KGM", i, $"{description} {i}", 100m, 118m, 100m * i, 18m * i)).ToList(),
        new PrintedTotals(200m, 0m, 0m, 0m, 36m, 236m),
        "20100066603|01|F001|123|36.00|236.00|2026-09-30|6|20100070970|AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    /// <summary>Content of every page, decompressed, as Latin-1 text.</summary>
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

    [Fact]
    public void The_pdf_is_structurally_valid_with_correct_cross_reference_offsets()
    {
        var pdf = _renderer.Render(Document()).Value;
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        var startxref = long.Parse(Regex.Match(text, @"startxref\n(\d+)\n").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring((int)startxref, 4));

        var entries = Regex.Matches(text[(int)startxref..], @"(\d{10}) 00000 n ").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(7, entries.Count); // catalog, page tree, 2 fonts, info, page, contents
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[entries[i]..], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("01", "FACTURA ELECTRÓNICA", "Representación impresa de la factura electrónica")]
    [InlineData("03", "BOLETA DE VENTA ELECTRÓNICA", "Representación impresa de la boleta de venta electrónica")]
    public void The_mandatory_content_of_the_annexes_is_printed(string type, string denomination, string legend)
    {
        var content = string.Concat(PageContents(_renderer.Render(Document(type)).Value));

        foreach (var expected in new[]
        {
            denomination, legend, "(RUC 20100066603)", type == "01" ? "(F001-123)" : "(B001-123)", "EMISORA DEMO S.A.C.", "Emisora Demo", "30/09/2026",
            @"SOLES \(S/\)", "Op. gravadas", "S/ 200.00", "IGV", "S/ 36.00", "IMPORTE TOTAL", "S/ 236.00", "SON: DOSCIENTOS TREINTA Y SEIS CON 00/100 SOLES",
            @"Resumen \(hash\):", "Servicio de consultoría 1",
        })
        {
            Assert.Contains(expected, content, StringComparison.Ordinal);
        }

        // The type code is replaced by its denomination, never printed as a bare code.
        Assert.DoesNotContain("(01)", content, StringComparison.Ordinal);
        Assert.Contains(type == "01" ? "Registro Unico de Contributentes:" : "Documento Nacional de Identidad:", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("07", "NOTA DE CRÉDITO ELECTRÓNICA", "Representación impresa de la nota de crédito electrónica")]
    [InlineData("08", "NOTA DE DÉBITO ELECTRÓNICA", "Representación impresa de la nota de débito electrónica")]
    public void Notes_print_what_they_modify_and_why(string type, string denomination, string legend)
    {
        var note = Document("01") with { DocumentTypeCode = type, Series = "FC01", Note = new PrintedNote("Factura electrónica F001-123", "Anulación de la operación") };

        var content = string.Concat(PageContents(_renderer.Render(note).Value));

        foreach (var expected in new[] { denomination, legend, "(FC01-123)", "Documento que modifica:", "Factura electrónica F001-123", "Motivo:", "Anulación de la operación" })
        {
            Assert.Contains(expected, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_note_without_the_document_it_modifies_is_not_printable()
    {
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _renderer.Render(Document("01") with { DocumentTypeCode = "07" }).Error.Code);
    }

    [Fact]
    public void Unit_codes_niu_and_zz_are_not_printed_but_others_are()
    {
        var content = string.Concat(PageContents(_renderer.Render(Document(lines: 2)).Value));

        Assert.Contains("(KGM)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("(NIU)", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Operation_types_that_do_not_apply_are_not_printed_and_free_ones_are()
    {
        var plain = string.Concat(PageContents(_renderer.Render(Document()).Value));
        Assert.DoesNotContain("Op. exoneradas", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Op. inafectas", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Op. gratuitas", plain, StringComparison.Ordinal);

        var mixed = Document() with { Totals = new PrintedTotals(200m, 50m, 30m, 10m, 36m, 316m) };
        var content = string.Concat(PageContents(_renderer.Render(mixed).Value));
        foreach (var label in new[] { "Op. exoneradas", "Op. inafectas", "Op. gratuitas" })
        {
            Assert.Contains(label, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_receipt_without_buyer_identification_prints_no_buyer_block()
    {
        var anonymous = Document("03") with { BuyerDocumentNumber = null, BuyerDocumentTypeName = null, BuyerName = null, BuyerAddress = null };

        var content = string.Concat(PageContents(_renderer.Render(anonymous).Value));

        Assert.DoesNotContain("Adquirente:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_charges_and_discounts_are_printed_only_when_they_exist()
    {
        var plain = string.Concat(PageContents(_renderer.Render(Document()).Value));
        var adjusted = string.Concat(PageContents(_renderer.Render(Document() with { Totals = new PrintedTotals(200m, 0m, 0m, 0m, 36m, 249m, 7m, 20m) }).Value));

        Assert.DoesNotContain("Otros cargos", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Otros descuentos", plain, StringComparison.Ordinal);
        Assert.Contains("(Otros cargos)", adjusted, StringComparison.Ordinal);
        Assert.Contains("(S/ 20.00)", adjusted, StringComparison.Ordinal);
        Assert.Contains("(Otros descuentos)", adjusted, StringComparison.Ordinal);
        Assert.Contains("(S/ -7.00)", adjusted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sale_on_credit_prints_the_payment_form_the_net_amount_and_the_installments()
    {
        var credit = Document() with { Installments = [new PrintedInstallment(1, new DateOnly(2026, 10, 30), 100m), new PrintedInstallment(2, new DateOnly(2026, 11, 30), 136m)] };

        var content = string.Concat(PageContents(_renderer.Render(credit).Value));

        Assert.Contains("(Forma de pago: Crédito)", content, StringComparison.Ordinal);
        Assert.Contains("(Monto neto pendiente de pago: S/ 236.00)", content, StringComparison.Ordinal);
        Assert.Contains("(Cuota 1)", content, StringComparison.Ordinal);
        Assert.Contains("(Vence: 30/11/2026)", content, StringComparison.Ordinal);
        Assert.Contains("(S/ 136.00)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Cuota", string.Concat(PageContents(_renderer.Render(Document()).Value)), StringComparison.Ordinal);
    }

    [Fact]
    public void Many_installments_paginate_and_the_totals_stay_on_the_last_page()
    {
        var installments = Enumerable.Range(1, 90).Select(i => new PrintedInstallment(i, new DateOnly(2026, 10, 1).AddDays(i), 2m)).ToList();

        var pages = PageContents(_renderer.Render(Document(lines: 20) with { Installments = installments }).Value);

        Assert.True(pages.Count >= 2);
        Assert.Equal(1, pages.Count(p => p.Contains("IMPORTE TOTAL", StringComparison.Ordinal)));
        Assert.Contains("(Cuota 90)", string.Concat(pages), StringComparison.Ordinal);
    }

    [Fact]
    public void The_ivap_is_printed_as_its_own_row_instead_of_a_zero_igv()
    {
        var plain = string.Concat(PageContents(_renderer.Render(Document()).Value));
        var rice = string.Concat(PageContents(_renderer.Render(Document() with { Totals = new PrintedTotals(100m, 0m, 0m, 0m, 0m, 104m, 0m, 0m, 4m) }).Value));

        // "IGV" is also a column heading of the lines: the totals row adds a second one.
        Assert.Equal(2, Regex.Count(plain, @"\(IGV\)", RegexOptions.None, TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("(IVAP)", plain, StringComparison.Ordinal);
        Assert.Contains("(IVAP)", rice, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(rice, @"\(IGV\)", RegexOptions.None, TimeSpan.FromSeconds(2)));
        Assert.Contains("(S/ 4.00)", rice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_voided_document_carries_the_word_ANULADO_on_every_page_and_the_rest_is_unchanged()
    {
        var plain = _renderer.Render(Document(lines: 120)).Value;
        var voided = _renderer.Render(Document(lines: 120) with { Voided = true }).Value;

        var plainPages = PageContents(plain);
        var voidedPages = PageContents(voided);
        Assert.DoesNotContain(plainPages, p => p.Contains("(ANULADO)", StringComparison.Ordinal));
        Assert.True(voidedPages.Count >= 3);
        Assert.Equal(plainPages.Count, voidedPages.Count);
        Assert.All(voidedPages, p => Assert.Equal(2, Regex.Count(p, @"\(ANULADO\) Tj", RegexOptions.None, TimeSpan.FromSeconds(2)))); // watermark and header label
        Assert.Contains("IMPORTE TOTAL", voidedPages[^1], StringComparison.Ordinal);
        Assert.Contains(@"Resumen \(hash\):", voidedPages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Long_documents_paginate_and_the_totals_and_qr_appear_once_on_the_last_page()
    {
        var pdf = _renderer.Render(Document(lines: 120)).Value;
        var pages = PageContents(pdf);

        Assert.True(pages.Count >= 3, $"expected several pages, got {pages.Count}");
        Assert.Contains(@"\(continuación\)", pages[1], StringComparison.Ordinal);
        Assert.Equal(1, pages.Count(p => p.Contains("IMPORTE TOTAL", StringComparison.Ordinal)));
        Assert.Contains("IMPORTE TOTAL", pages[^1], StringComparison.Ordinal);
        Assert.Equal(1, pages.Count(p => p.Contains(@"Resumen \(hash\):", StringComparison.Ordinal)));
    }

    [Fact]
    public void Text_is_escaped_so_hostile_descriptions_cannot_break_the_pdf()
    {
        var hostile = Document(description: "Cable (USB) \\ ) ET Q 0 0 0 rg ñandú €");

        var content = string.Concat(PageContents(_renderer.Render(hostile).Value));

        Assert.Contains(@"Cable \(USB\) \\ \) ET Q 0 0 0 rg ñandú ?", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_descriptions_wrap_inside_their_column()
    {
        var description = string.Join(' ', Enumerable.Repeat("descripcion", 30));

        var content = string.Concat(PageContents(_renderer.Render(Document(lines: 1, description: description)).Value));

        Assert.True(Regex.Count(content, @"\(descripcion[^)]*\) Tj", RegexOptions.None, TimeSpan.FromSeconds(5)) > 2);
    }

    [Fact]
    public void Output_is_deterministic_and_depends_on_the_qr_payload()
    {
        var first = _renderer.Render(Document()).Value;

        Assert.Equal(first, _renderer.Render(Document()).Value);
        Assert.NotEqual(first, _renderer.Render(Document() with { QrPayload = "otro|contenido" }).Value);
    }

    [Fact]
    public void Unprintable_documents_are_refused()
    {
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _renderer.Render(Document("05")).Error.Code);
        Assert.False(_renderer.Render(Document() with { Lines = [] }).IsSuccess);
        Assert.False(_renderer.Render(Document() with { QrPayload = " " }).IsSuccess);
    }

    [Fact]
    public void The_font_width_tables_cover_the_printable_ascii_range()
    {
        // 95 printable characters from space to tilde: the table length is what keeps the alignment honest.
        var width = Helvetica.Width(PdfFont.Regular, new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray()), 1000);
        var bold = Helvetica.Width(PdfFont.Bold, new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray()), 1000);

        Assert.True(width is > 40_000 and < 60_000);
        Assert.True(bold > width);
        Assert.Equal(Helvetica.Width(PdfFont.Regular, "n", 10), Helvetica.Width(PdfFont.Regular, "ñ", 10));
        Assert.Equal(5.56, Helvetica.Width(PdfFont.Regular, "0", 10), 2);
    }

    [Fact]
    public void The_qr_follows_the_official_symbol_parameters()
    {
        // S19 §6.4: QR Code 2005, level Q, UTF-8. QRCoder keeps a 4-module quiet zone around the symbol, which the renderer strips.
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode("20100066603|01|F001|123|36.00|236.00|2026-09-30|6|20100070970|digest", QRCodeGenerator.ECCLevel.Q, forceUtf8: true);

        var symbolModules = data.ModuleMatrix.Count - 8;

        Assert.Equal(0, (symbolModules - 17) % 4); // 17 + 4 × version
        Assert.True(symbolModules is >= 21 and <= 177);
        // At 100 pt for the symbol, each module is far above the 0.19 mm minimum and the whole image far below 6 cm.
        Assert.True((100.0 - (2 * 2.835)) / symbolModules * 25.4 / 72.0 > 0.19);
    }
}
