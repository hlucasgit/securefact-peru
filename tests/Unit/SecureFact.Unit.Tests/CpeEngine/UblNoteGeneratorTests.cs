using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using ExcelDataReader;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.TaxEngine;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Unit.Tests.CpeEngine;

public class UblNoteGeneratorTests
{
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XmlNamespaceManager Namespaces = BuildNamespaces();

    private readonly UblInvoiceGenerator _generator = new();

    private static XmlNamespaceManager BuildNamespaces()
    {
        var manager = new XmlNamespaceManager(new NameTable());
        manager.AddNamespace("cn", "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2");
        manager.AddNamespace("dn", "urn:oasis:names:specification:ubl:schema:xsd:DebitNote-2");
        manager.AddNamespace("cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
        manager.AddNamespace("cbc", "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2");
        manager.AddNamespace("ext", Ext.NamespaceName);
        manager.AddNamespace("ds", Ds.NamespaceName);
        return manager;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureFact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    internal static UblNoteData Note(string type = "07", string reason = "01", string referencedType = "01", params (string Code, decimal Qty, decimal Unit)[] lines)
    {
        lines = lines.Length > 0 ? lines : [("10", 1m, 100m)];
        var taxLines = lines.Select(l => new TaxableLine(l.Qty, l.Unit, l.Code)).ToList();
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest(taxLines, new TaxRates(0.18m))).Value;
        var ublLines = lines.Select((l, i) => new UblLine(i + 1, $"Producto {i + 1}", "NIU", $"P{i + 1:000}", l.Qty, l.Unit, null, l.Code)).ToList();
        return new UblNoteData(
            type, referencedType == "01" ? "FC01" : "BC01", 7, new DateOnly(2026, 10, 1), new TimeOnly(9, 30, 0), "PEN", reason,
            type == "07" ? "Anulación de la operación" : "Aumento en el valor", referencedType, referencedType == "01" ? "F001" : "B001", 123,
            new UblParty("6", "20100066603", "EMISORA DEMO SAC", "Emisora Demo"),
            referencedType == "01" ? new UblParty("6", "20100070970", "CLIENTE DEMO SAC") : new UblParty("1", "12345678", "JUAN PEREZ"),
            ublLines, totals, 0.18m);
    }

    /// <summary>A credit note of reason 13: nothing is sold (the line is worth zero, the payable amount is zero) and the new installments are stated.</summary>
    internal static UblNoteData Adjustment() =>
        Note("07", "13", "01", ("10", 1m, 0m)) with { Installments = [new(40m, new DateOnly(2026, 11, 15)), new(78m, new DateOnly(2026, 12, 15))] };

    /// <summary>A credit note of reason 12: an adjustment of an operation taxed with the IVAP, whose lines are IVAP lines too.</summary>
    internal static UblNoteData IvapAdjustment(string reason = "12", string affectation = "17", decimal ivapRate = 0.04m)
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1m, 100m, affectation)], new TaxRates(0.18m, 0.04m))).Value;
        return Note("07", reason, "01") with
        {
            Totals = totals,
            Lines = [new UblLine(1, "Arroz pilado", "KGM", null, 1m, 100m, null, affectation)],
            IvapRate = ivapRate,
        };
    }

    /// <summary>A credit note of reason 11: an adjustment of an export, with export lines only.</summary>
    internal static UblNoteData ExportAdjustment(string reason = "11")
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1m, 100m, "40")], new TaxRates(0.18m))).Value;
        return Note("07", reason, "01") with { Totals = totals, Lines = [new UblLine(1, "Bien de exportación", "NIU", null, 1m, 100m, null, "40")] };
    }

    private static UblNoteData ExportMixed()
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1m, 100m, "40"), new TaxableLine(1m, 100m, "10")], new TaxRates(0.18m))).Value;
        return Note("07", "01", "01") with
        {
            Totals = totals,
            Lines = [new UblLine(1, "Bien de exportación", "NIU", null, 1m, 100m, null, "40"), new UblLine(2, "Bien nacional", "NIU", null, 1m, 100m, null, "10")],
        };
    }

    private static List<string> SchemaErrors(XDocument document, string type)
    {
        var copy = new XDocument(document);
        copy.Descendants(Ext + "ExtensionContent").First().Add(
            new XElement(
                Ds + "Signature",
                new XAttribute(XNamespace.Xmlns + "ds", Ds.NamespaceName),
                new XElement(
                    Ds + "SignedInfo",
                    new XElement(Ds + "CanonicalizationMethod", new XAttribute("Algorithm", "http://www.w3.org/TR/2001/REC-xml-c14n-20010315")),
                    new XElement(Ds + "SignatureMethod", new XAttribute("Algorithm", "http://www.w3.org/2000/09/xmldsig#rsa-sha1")),
                    new XElement(
                        Ds + "Reference",
                        new XAttribute("URI", string.Empty),
                        new XElement(Ds + "DigestMethod", new XAttribute("Algorithm", "http://www.w3.org/2000/09/xmldsig#sha1")),
                        new XElement(Ds + "DigestValue", "AAAAAAAAAAAAAAAAAAAAAAAAAAA="))),
                new XElement(Ds + "SignatureValue", "AAAA")));

        var root = Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.1");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(
            type == "07" ? "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2" : "urn:oasis:names:specification:ubl:schema:xsd:DebitNote-2",
            Path.Combine(root, "maindoc", type == "07" ? "UBL-CreditNote-2.1.xsd" : "UBL-DebitNote-2.1.xsd"));
        using var dsig = XmlReader.Create(Path.Combine(root, "common", "UBL-xmldsig-core-schema-2.1.xsd"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);

        var errors = new List<string>();
        copy.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    [Theory]
    [InlineData("07", "01")]
    [InlineData("07", "03")]
    [InlineData("08", "01")]
    [InlineData("08", "03")]
    public void Notes_validate_against_the_official_schemas(string type, string referencedType)
    {
        var result = _generator.GenerateNote(Note(type, type == "07" ? "07" : "02", referencedType, ("10", 2m, 33.3333333333m), ("20", 1m, 50m), ("30", 1m, 10m)));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(SchemaErrors(XDocument.Parse(result.Value.Xml), type));
    }

    [Fact]
    public void A_credit_note_carries_the_reason_the_reference_and_its_own_line_names()
    {
        var result = _generator.GenerateNote(Note("07", "01"));
        var xml = XDocument.Parse(result.Value.Xml);

        Assert.Equal("20100066603-07-FC01-7", result.Value.FileBaseName);
        Assert.Equal("FC01-7", xml.XPathSelectElement("/cn:CreditNote/cbc:ID", Namespaces)!.Value);
        Assert.Equal("01", xml.XPathSelectElement("/cn:CreditNote/cac:DiscrepancyResponse/cbc:ResponseCode", Namespaces)!.Value);
        Assert.Equal("Anulación de la operación", xml.XPathSelectElement("/cn:CreditNote/cac:DiscrepancyResponse/cbc:Description", Namespaces)!.Value);
        Assert.Equal("F001-123", xml.XPathSelectElement("/cn:CreditNote/cac:DiscrepancyResponse/cbc:ReferenceID", Namespaces)!.Value);
        Assert.Equal("F001-123", xml.XPathSelectElement("/cn:CreditNote/cac:BillingReference/cac:InvoiceDocumentReference/cbc:ID", Namespaces)!.Value);
        Assert.Equal("01", xml.XPathSelectElement("/cn:CreditNote/cac:BillingReference/cac:InvoiceDocumentReference/cbc:DocumentTypeCode", Namespaces)!.Value);
        Assert.NotNull(xml.XPathSelectElement("/cn:CreditNote/cac:CreditNoteLine/cbc:CreditedQuantity", Namespaces));
        Assert.NotNull(xml.XPathSelectElement("/cn:CreditNote/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces));
        Assert.Empty(xml.XPathSelectElements("//cbc:InvoiceTypeCode", Namespaces));
    }

    [Fact]
    public void A_debit_note_uses_its_own_line_and_total_names()
    {
        var xml = XDocument.Parse(_generator.GenerateNote(Note("08", "02")).Value.Xml);

        Assert.NotNull(xml.XPathSelectElement("/dn:DebitNote/cac:DebitNoteLine/cbc:DebitedQuantity", Namespaces));
        Assert.NotNull(xml.XPathSelectElement("/dn:DebitNote/cac:RequestedMonetaryTotal/cbc:PayableAmount", Namespaces));
        Assert.Equal("08", Regex.Match(_generator.GenerateNote(Note("08", "02")).Value.FileBaseName, "-(08)-", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value);
    }

    // ---------- mandatory tags of the official workbook ----------

    private static readonly Regex Comment = new(@"\s*\(.*$", RegexOptions.Singleline);

    private static IEnumerable<string> MandatoryPaths(string sheet, string root)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.OpenRead(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx"));
        using var reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            if (reader.Name != sheet)
            {
                continue;
            }

            while (reader.Read())
            {
                var condition = reader.FieldCount > 4 ? reader.GetValue(4)?.ToString()?.Trim() : null;
                var tag = reader.FieldCount > 7 ? reader.GetValue(7)?.ToString()?.Trim() : null;
                if (condition == "M" && tag is not null && tag.StartsWith(root, StringComparison.Ordinal))
                {
                    var path = Comment.Replace(tag, string.Empty);
                    var at = path.IndexOf('@', StringComparison.Ordinal);
                    // Some cells list two paths separated by a space (the signature in two places): keep the first.
                    yield return (at >= 0 ? path[..at] : path).Split(' ')[0];
                }
            }

            yield break;
        }
        while (reader.NextResult());
    }

    [Theory]
    [InlineData("07", "NotaCredito2_0", "/CreditNote", "/cn:CreditNote")]
    [InlineData("08", "NotaDebito2_0", "/DebitNote", "/dn:DebitNote")]
    public void Every_mandatory_tag_of_the_workbook_is_present(string type, string sheet, string root, string prefixed)
    {
        var xml = XDocument.Parse(_generator.GenerateNote(Note(type, type == "07" ? "07" : "02")).Value.Xml);
        // The XMLDSig signature itself is added by the signer, not by the generator (its own tests cover it).
        var paths = MandatoryPaths(sheet, root).Distinct().Where(p => !p.Contains("ds:Signature", StringComparison.Ordinal)).ToList();
        Assert.True(paths.Count >= 15, $"Expected the workbook to list mandatory tags, found {paths.Count}.");

        var missing = paths
            .Select(p => prefixed + p[root.Length..])
            .Where(p => !xml.XPathSelectElements(p, Namespaces).Any())
            .ToList();

        Assert.True(missing.Count == 0, "Missing mandatory tags: " + string.Join("; ", missing));
    }

    [Fact]
    public void The_reason_codes_exist_in_the_official_catalogues()
    {
        var catalogs = SecureFact.CatalogImporter.CatalogWorkbookParser
            .Parse(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx")).Catalogs;

        var credit = catalogs.Single(c => c.Number == "09").Entries.Select(e => e.Code).ToHashSet();
        var debit = catalogs.Single(c => c.Number == "10").Entries.Select(e => e.Code).ToHashSet();

        foreach (var code in new[] { "01", "02", "03", "04", "05", "06", "07", "08", "09", "10" })
        {
            Assert.Contains(code, credit);
        }

        foreach (var code in new[] { "01", "02", "03" })
        {
            Assert.Contains(code, debit);
        }
    }

    // ---------- reason 13: adjustment of installments ----------

    [Fact]
    public void A_credit_note_of_reason_13_validates_against_the_schema_and_states_the_new_installments()
    {
        var result = _generator.GenerateNote(Adjustment());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml, "07"));
        Assert.Equal("13", xml.XPathSelectElement("/cn:CreditNote/cac:DiscrepancyResponse/cbc:ResponseCode", Namespaces)!.Value);
        var terms = xml.XPathSelectElements("/cn:CreditNote/cac:PaymentTerms", Namespaces).ToList();
        Assert.Equal(["Credito", "Cuota001", "Cuota002"], terms.Select(t => t.XPathSelectElement("cbc:PaymentMeansID", Namespaces)!.Value).ToArray());
        Assert.Equal(["118.00", "40.00", "78.00"], terms.Select(t => t.XPathSelectElement("cbc:Amount", Namespaces)!.Value).ToArray());
        Assert.Equal(["2026-11-15", "2026-12-15"], terms.Skip(1).Select(t => t.XPathSelectElement("cbc:PaymentDueDate", Namespaces)!.Value).ToArray());
        Assert.Equal("0.00", xml.XPathSelectElement("/cn:CreditNote/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value); // rule 3315
    }

    [Fact]
    public void Notes_of_other_reasons_state_no_payment_terms()
    {
        var xml = XDocument.Parse(_generator.GenerateNote(Note("07", "01")).Value.Xml);

        Assert.Empty(xml.XPathSelectElements("//cac:PaymentTerms", Namespaces));
    }

    // ---------- reason 12: adjustment of IVAP operations ----------

    [Fact]
    public void A_credit_note_of_reason_12_validates_against_the_schema_and_states_the_ivap()
    {
        var result = _generator.GenerateNote(IvapAdjustment());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml, "07"));
        Assert.Equal("12", xml.XPathSelectElement("/cn:CreditNote/cac:DiscrepancyResponse/cbc:ResponseCode", Namespaces)!.Value);
        Assert.Equal("2007", xml.XPathSelectElement("/cn:CreditNote/cbc:Note", Namespaces)!.Attribute("languageLocaleID")!.Value);
        var line = xml.XPathSelectElement("/cn:CreditNote/cac:CreditNoteLine/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory", Namespaces)!;
        Assert.Equal("1016", line.XPathSelectElement("cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("4.00", line.XPathSelectElement("cbc:Percent", Namespaces)!.Value);
        Assert.Equal("104.00", xml.XPathSelectElement("/cn:CreditNote/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
    }

    // ---------- reason 11: adjustment of export operations ----------

    [Fact]
    public void A_credit_note_of_reason_11_validates_against_the_schema_and_states_tax_9995()
    {
        var result = _generator.GenerateNote(ExportAdjustment());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml, "07"));
        Assert.Equal("11", xml.XPathSelectElement("/cn:CreditNote/cac:DiscrepancyResponse/cbc:ResponseCode", Namespaces)!.Value);
        var subtotal = xml.XPathSelectElement("/cn:CreditNote/cac:TaxTotal/cac:TaxSubtotal", Namespaces)!;
        Assert.Equal("9995", subtotal.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("100.00", xml.XPathSelectElement("/cn:CreditNote/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
    }

    [Fact]
    public void Another_reason_may_carry_export_lines_when_they_are_all_export_lines()
    {
        Assert.True(_generator.GenerateNote(ExportAdjustment("01")).IsSuccess);
    }

    // ---------- refusals ----------

    public static TheoryData<string, UblNoteData, string> InvalidData() => new()
    {
        { "invoice type", Note() with { DocumentTypeCode = "01" }, ErrorCodes.CpeInvalidDocument },
        { "credit reason 11 with taxed lines", Note("07", "11"), ErrorCodes.CpeInvalidDocument },
        { "reason 11 on a debit note", ExportAdjustment() with { DocumentTypeCode = "08" }, ErrorCodes.CpeUnsupported },
        { "reason 11 on a receipt", ExportAdjustment() with { ReferencedDocumentTypeCode = "03", ReferencedSeries = "B001" }, ErrorCodes.CpeInvalidDocument },
        { "export lines mixed with others", ExportMixed(), ErrorCodes.CpeInvalidDocument },
        { "guest data in a note", Note() with { Lines = [Note().Lines[0] with { Guest = new UblGuest("John Smith", "7", "X1234567", "US") }] }, ErrorCodes.CpeInvalidDocument },
        { "fishing data in a note", Note() with { Lines = [Note().Lines[0] with { Fishing = new UblFishing("CO-1", "LUANA II", "Anchoveta", "Mollendo", new DateOnly(2026, 9, 28), 10m) }] }, ErrorCodes.CpeInvalidDocument },
        { "cargo transport data in a note", Note() with { Lines = [Note().Lines[0] with { Transport = new UblCargoTransport("150101", "Lima", "040101", "Arequipa", "Viaje", 1m, 1m, 1m) }] }, ErrorCodes.CpeInvalidDocument },
        { "credit reason 13 without installments", Note("07", "13"), ErrorCodes.CpeInvalidDocument },
        { "credit reason 13 with a payable amount", Note("07", "13") with { Installments = [new(118m, new DateOnly(2026, 11, 1))] }, ErrorCodes.CpeInvalidDocument },
        { "installments on another reason", Note("07", "01") with { Installments = [new(118m, new DateOnly(2026, 11, 1))] }, ErrorCodes.CpeInvalidDocument },
        { "reason 13 on a receipt", Adjustment() with { ReferencedDocumentTypeCode = "03", ReferencedSeries = "B001" }, ErrorCodes.CpeInvalidDocument },
        { "reason 13 with a zero installment", Adjustment() with { Installments = [new(0m, new DateOnly(2026, 11, 1))] }, ErrorCodes.CpeInvalidDocument },
        { "reason 13 on a debit note", Adjustment() with { DocumentTypeCode = "08" }, ErrorCodes.CpeUnsupported },
        { "reason 12 with IGV lines", IvapAdjustment("12", "10"), ErrorCodes.CpeInvalidDocument },
        { "IVAP lines on another reason", IvapAdjustment("01"), ErrorCodes.CpeInvalidDocument },
        { "reason 12 without the ivap rate", IvapAdjustment() with { IvapRate = 0m }, ErrorCodes.CpeInvalidDocument },
        { "reason 12 on a debit note", IvapAdjustment() with { DocumentTypeCode = "08" }, ErrorCodes.CpeUnsupported },
        { "debit reason 10", Note("08", "10"), ErrorCodes.CpeUnsupported },
        { "no reason text", Note() with { ReasonDescription = " " }, ErrorCodes.CpeInvalidDocument },
        { "long reason", Note() with { ReasonDescription = new string('x', 501) }, ErrorCodes.CpeInvalidDocument },
        { "reason with newline", Note() with { ReasonDescription = "a\nb" }, ErrorCodes.CpeInvalidDocument },
        { "reference type", Note() with { ReferencedDocumentTypeCode = "07" }, ErrorCodes.CpeInvalidDocument },
        { "reference number", Note() with { ReferencedNumber = 0 }, ErrorCodes.CpeInvalidDocument },
        { "no series", Note() with { Series = " " }, ErrorCodes.CpeInvalidDocument },
    };

    [Theory]
    [MemberData(nameof(InvalidData))]
    public void Invalid_or_unsupported_notes_are_refused_not_emitted(string name, UblNoteData data, string code)
    {
        var result = _generator.GenerateNote(data);

        Assert.False(result.IsSuccess, name);
        Assert.Equal(code, result.Error.Code);
    }

    [Theory]
    [InlineData("07", "cn", "CreditNote", "CreditNoteLine")]
    [InlineData("08", "dn", "DebitNote", "DebitNoteLine")]
    public void A_note_states_the_isc_and_the_plastic_bags_of_its_lines(string type, string prefix, string root, string line)
    {
        var rates = new TaxRates(0.18m, IcbperUnitAmount: 0.50m);
        var isc = new IscInput(IscSystem.AdValorem, 0.10m);
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(2, 100m, "10", Isc: isc, PlasticBagCount: 2)], rates)).Value;
        var data = Note(type) with
        {
            Lines = [new UblLine(1, "Producto", "NIU", "P001", 2, 100m, null, "10", Isc: isc, PlasticBagCount: 2)],
            Totals = totals,
            IcbperUnitAmount = 0.50m,
        };

        var result = _generator.GenerateNote(data);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml, type));
        var ids = xml.XPathSelectElements($"/{prefix}:{root}/cac:{line}/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces).Select(e => e.Value);
        Assert.Equal(["1000", "2000", "7152"], ids);
        Assert.Equal(["1000", "2000", "7152"], xml.XPathSelectElements($"/{prefix}:{root}/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces).Select(e => e.Value));
        Assert.Equal("2", xml.XPathSelectElement($"/{prefix}:{root}/cac:{line}/cac:TaxTotal/cac:TaxSubtotal/cbc:BaseUnitMeasure", Namespaces)!.Value);
    }

    // ---------- detraction on a debit note (sheet NotaDebito2_0, rules 3313, 3314, 3127, 3033-3037, 3208) ----------

    private static UblNoteData WithDetraction(string type = "08", string referencedType = "01", string currency = "PEN", decimal amount = 14.16m) =>
        Note(type, type == "07" ? "07" : "02", referencedType) with { Currency = currency, Detraction = new UblDetraction("037", 12m, amount, "00012345678") };

    [Fact]
    public void A_debit_note_states_its_detraction_as_the_account_and_the_terms_before_the_taxes_and_validates_against_the_schema()
    {
        var result = _generator.GenerateNote(WithDetraction());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml, "08"));
        Assert.Equal("Detraccion", xml.XPathSelectElement("/dn:DebitNote/cac:PaymentMeans/cbc:ID", Namespaces)!.Value);
        Assert.Equal("001", xml.XPathSelectElement("/dn:DebitNote/cac:PaymentMeans/cbc:PaymentMeansCode", Namespaces)!.Value);
        Assert.Equal("00012345678", xml.XPathSelectElement("/dn:DebitNote/cac:PaymentMeans/cac:PayeeFinancialAccount/cbc:ID", Namespaces)!.Value);
        Assert.Equal("Detraccion", xml.XPathSelectElement("/dn:DebitNote/cac:PaymentTerms/cbc:ID", Namespaces)!.Value);
        Assert.Equal("037", xml.XPathSelectElement("/dn:DebitNote/cac:PaymentTerms/cbc:PaymentMeansID", Namespaces)!.Value);
        Assert.Equal("12.00", xml.XPathSelectElement("/dn:DebitNote/cac:PaymentTerms/cbc:PaymentPercent", Namespaces)!.Value);
        var amount = xml.XPathSelectElement("/dn:DebitNote/cac:PaymentTerms/cbc:Amount", Namespaces)!;
        Assert.Equal(("14.16", "PEN"), (amount.Value, amount.Attribute("currencyID")!.Value));
        // UBL order: the means, then the terms, then the tax total.
        var order = xml.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(order.IndexOf("PaymentMeans") < order.IndexOf("PaymentTerms") && order.IndexOf("PaymentTerms") < order.IndexOf("TaxTotal"));
        Assert.Empty(xml.XPathSelectElements("//cbc:InvoiceTypeCode", Namespaces)); // a note has no operation type: the sheet ties none to its detraction
    }

    [Fact]
    public void A_note_without_detraction_has_no_payment_means_and_a_credit_note_never_carries_one()
    {
        Assert.Empty(XDocument.Parse(_generator.GenerateNote(Note("08", "02")).Value.Xml).XPathSelectElements("//cac:PaymentMeans", Namespaces));

        var credit = _generator.GenerateNote(WithDetraction("07"));
        Assert.False(credit.IsSuccess);
        Assert.Contains("nota de débito", credit.Error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("08", "03", "PEN", 14.16)] // on a receipt
    [InlineData("08", "01", "USD", 14.16)] // not in soles
    [InlineData("08", "01", "PEN", 0)] // no amount
    [InlineData("08", "01", "PEN", 500)] // more than the payable amount of the note
    public void A_detraction_that_the_sheet_does_not_allow_is_refused(string type, string referencedType, string currency, double amount)
    {
        var result = _generator.GenerateNote(WithDetraction(type, referencedType, currency, (decimal)amount));

        Assert.False(result.IsSuccess);
    }
}
