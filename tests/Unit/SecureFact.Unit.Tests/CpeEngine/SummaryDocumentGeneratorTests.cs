using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using ExcelDataReader;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;

namespace SecureFact.Unit.Tests.CpeEngine;

public class SummaryDocumentGeneratorTests
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    private static readonly XmlNamespaceManager Namespaces = BuildNamespaces();

    private readonly SummaryDocumentGenerator _generator = new();

    private static XmlNamespaceManager BuildNamespaces()
    {
        var manager = new XmlNamespaceManager(new NameTable());
        manager.AddNamespace("sum", "urn:sunat:names:specification:ubl:peru:schema:xsd:SummaryDocuments-1");
        manager.AddNamespace("sac", "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1");
        manager.AddNamespace("cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
        manager.AddNamespace("cbc", Cbc.NamespaceName);
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

    internal static SummaryData Data(params SummaryLineData[] lines) =>
        new("20100066603", "EMISORA DEMO SAC", new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1), 1,
            lines.Length > 0 ? lines : [Taxed(1, "B001", 1), Exempt(2, "B001", 2)]);

    internal static SummaryLineData Taxed(int line, string series, long number, string currency = "PEN") =>
        new(line, series, number, "1", "12345678", currency, 118m, 100m, 0m, 0m, 18m, 0.18m);

    internal static SummaryLineData Exempt(int line, string series, long number) =>
        new(line, series, number, null, null, "PEN", 50m, 0m, 50m, 0m, 0m, 0.18m);

    private static List<string> SchemaErrors(XDocument document, bool addDummySignature = true)
    {
        var copy = new XDocument(document);
        if (addDummySignature)
        {
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
        }

        var root = Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.0");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add("urn:sunat:names:specification:ubl:peru:schema:xsd:SummaryDocuments-1", Path.Combine(root, "maindoc", "UBLPE-SummaryDocuments-1.0.xsd"));
        // The official xmldsig schema carries an internal DTD; parsing it is safe here because it is a local, versioned file.
        using var dsig = XmlReader.Create(Path.Combine(root, "common", "xmldsig-core-schema.xsd"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);

        var errors = new List<string>();
        copy.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    [Fact]
    public void A_summary_validates_against_the_official_schema()
    {
        var result = _generator.Generate(Data());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(SchemaErrors(XDocument.Parse(result.Value.Xml)));
    }

    [Fact]
    public void The_identifier_and_file_names_follow_the_manual()
    {
        var document = _generator.Generate(Data()).Value;

        Assert.Equal("RC-20261001-1", document.Identifier);
        Assert.Equal("20100066603-RC-20261001-1", document.FileBaseName);
        Assert.Equal("20100066603-RC-20261001-1.zip", document.ZipFileName);
        Assert.Matches(@"^[R][C]-[0-9]{8}-[0-9]{1,5}$", document.Identifier); // the pattern of rule 03 of the workbook
        var xml = XDocument.Parse(document.Xml);
        Assert.Equal("RC-20261001-1", xml.XPathSelectElement("/sum:SummaryDocuments/cbc:ID", Namespaces)!.Value);
        Assert.Equal("2026-09-30", xml.XPathSelectElement("/sum:SummaryDocuments/cbc:ReferenceDate", Namespaces)!.Value);
        Assert.Equal("2026-10-01", xml.XPathSelectElement("/sum:SummaryDocuments/cbc:IssueDate", Namespaces)!.Value);
        Assert.Equal("2.0", xml.XPathSelectElement("/sum:SummaryDocuments/cbc:UBLVersionID", Namespaces)!.Value);
        Assert.Equal("1.1", xml.XPathSelectElement("/sum:SummaryDocuments/cbc:CustomizationID", Namespaces)!.Value);
    }

    [Fact]
    public void Lines_carry_amounts_buyer_status_and_the_igv_rate()
    {
        var xml = XDocument.Parse(_generator.Generate(Data()).Value.Xml);

        var lines = xml.XPathSelectElements("/sum:SummaryDocuments/sac:SummaryDocumentsLine", Namespaces).ToList();
        Assert.Equal(2, lines.Count);

        var taxed = lines[0];
        Assert.Equal("B001-1", taxed.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("03", taxed.XPathSelectElement("cbc:DocumentTypeCode", Namespaces)!.Value);
        Assert.Equal("12345678", taxed.XPathSelectElement("cac:AccountingCustomerParty/cbc:CustomerAssignedAccountID", Namespaces)!.Value);
        Assert.Equal("1", taxed.XPathSelectElement("cac:AccountingCustomerParty/cbc:AdditionalAccountID", Namespaces)!.Value);
        Assert.Equal("1", taxed.XPathSelectElement("cac:Status/cbc:ConditionCode", Namespaces)!.Value);
        Assert.Equal("118.00", taxed.XPathSelectElement("sac:TotalAmount", Namespaces)!.Value);
        Assert.Equal("PEN", taxed.XPathSelectElement("sac:TotalAmount", Namespaces)!.Attribute("currencyID")!.Value);
        var payment = Assert.Single(taxed.XPathSelectElements("sac:BillingPayment", Namespaces));
        Assert.Equal("01", payment.XPathSelectElement("cbc:InstructionID", Namespaces)!.Value);
        Assert.Equal("100.00", payment.XPathSelectElement("cbc:PaidAmount", Namespaces)!.Value);
        Assert.Equal("18.00", taxed.XPathSelectElement("cac:TaxTotal/cbc:TaxAmount", Namespaces)!.Value);
        Assert.Equal("18.00", taxed.XPathSelectElement("cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:Percent", Namespaces)!.Value);
        Assert.Equal("1000", taxed.XPathSelectElement("cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);

        var exempt = lines[1];
        Assert.Empty(exempt.XPathSelectElements("cac:AccountingCustomerParty", Namespaces)); // no buyer, total not above S/ 700
        Assert.Equal("02", Assert.Single(exempt.XPathSelectElements("sac:BillingPayment", Namespaces)).XPathSelectElement("cbc:InstructionID", Namespaces)!.Value);
        Assert.Equal("0.00", exempt.XPathSelectElement("cac:TaxTotal/cbc:TaxAmount", Namespaces)!.Value); // IGV is always informed (rule 2278)
    }

    internal static SummaryLineData NoteLine(int line, string type, string series, long number, string referencedSeries = "B001", long referenced = 1, string referencedType = "03") =>
        new(line, series, number, "1", "12345678", "PEN", 118m, 100m, 0m, 0m, 18m, 0.18m, type, referencedType, referencedSeries, referenced);

    [Fact]
    public void Notes_of_receipts_validate_against_the_schema_and_carry_the_receipt_they_modify()
    {
        var data = Data(Taxed(1, "B001", 1), NoteLine(2, "07", "BC01", 1), NoteLine(3, "08", "BD01", 1));

        var result = _generator.Generate(data);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml));
        var lines = xml.XPathSelectElements("/sum:SummaryDocuments/sac:SummaryDocumentsLine", Namespaces).ToList();
        Assert.Equal(["03", "07", "08"], lines.Select(l => l.XPathSelectElement("cbc:DocumentTypeCode", Namespaces)!.Value).ToArray());
        Assert.Empty(lines[0].XPathSelectElements("cac:BillingReference", Namespaces));
        var reference = lines[1].XPathSelectElement("cac:BillingReference/cac:InvoiceDocumentReference", Namespaces)!;
        Assert.Equal("B001-1", reference.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("03", reference.XPathSelectElement("cbc:DocumentTypeCode", Namespaces)!.Value);
    }

    [Fact]
    public void Other_charges_are_informed_in_an_allowance_charge_node_and_enter_the_total()
    {
        var charged = Taxed(1, "B001", 1) with { TotalAmount = 123m, OtherCharges = 5m };

        var result = _generator.Generate(Data(charged));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml));
        var node = xml.XPathSelectElement("//sac:SummaryDocumentsLine/cac:AllowanceCharge", Namespaces)!;
        Assert.Equal("true", node.XPathSelectElement("cbc:ChargeIndicator", Namespaces)!.Value);
        Assert.Equal("5.00", node.XPathSelectElement("cbc:Amount", Namespaces)!.Value);
        Assert.Equal("123.00", xml.XPathSelectElement("//sac:SummaryDocumentsLine/sac:TotalAmount", Namespaces)!.Value);
    }

    [Fact]
    public void Other_discounts_only_lower_the_total_and_a_total_that_ignores_them_is_refused()
    {
        var discounted = Taxed(1, "B001", 1) with { TotalAmount = 108m, OtherDiscounts = 10m };

        var result = _generator.Generate(Data(discounted));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(XDocument.Parse(result.Value.Xml).XPathSelectElements("//sac:SummaryDocumentsLine/cac:AllowanceCharge", Namespaces));
        Assert.False(_generator.Generate(Data(Taxed(1, "B001", 1) with { OtherDiscounts = 10m })).IsSuccess);
        Assert.False(_generator.Generate(Data(Taxed(1, "B001", 1) with { OtherCharges = -1m })).IsSuccess);
    }

    [Fact]
    public void A_receipt_taxed_with_the_ivap_states_tax_1016_with_its_own_rate_and_validates_against_the_schema()
    {
        var ivap = Taxed(1, "B001", 1) with { TotalAmount = 104m, TaxedAmount = 100m, IgvAmount = 4m, IgvRate = 0.04m, IsIvap = true };

        var result = _generator.Generate(Data(ivap, Taxed(2, "B001", 2)));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml));
        var schemes = xml.XPathSelectElements("//sac:SummaryDocumentsLine/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory", Namespaces).ToList();
        Assert.Equal(["1016", "1000"], schemes.Select(c => c.XPathSelectElement("cac:TaxScheme/cbc:ID", Namespaces)!.Value).ToArray());
        Assert.Equal(["IVAP", "IGV"], schemes.Select(c => c.XPathSelectElement("cac:TaxScheme/cbc:Name", Namespaces)!.Value).ToArray());
        Assert.Equal(["4.00", "18.00"], schemes.Select(c => c.XPathSelectElement("cbc:Percent", Namespaces)!.Value).ToArray());
    }

    [Fact]
    public void A_line_with_status_3_voids_a_document_and_validates_against_the_schema()
    {
        var result = _generator.Generate(Data(Taxed(1, "B001", 1) with { Status = "3" }, NoteLine(2, "07", "BC01", 1) with { Status = "3" }));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = XDocument.Parse(result.Value.Xml);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal(["3", "3"], xml.XPathSelectElements("//sac:SummaryDocumentsLine/cac:Status/cbc:ConditionCode", Namespaces).Select(e => e.Value).ToArray());
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("")]
    public void Statuses_other_than_add_and_void_are_refused(string status) =>
        Assert.False(_generator.Generate(Data(Taxed(1, "B001", 1) with { Status = status })).IsSuccess);

    [Fact]
    public void The_same_document_added_and_voided_in_one_file_is_refused() =>
        Assert.False(_generator.Generate(Data(Taxed(1, "B001", 1), Taxed(2, "B001", 1) with { Status = "3" })).IsSuccess);

    [Fact]
    public void A_receipt_and_a_note_may_share_series_and_number_because_the_type_differs()
    {
        Assert.True(_generator.Generate(Data(Taxed(1, "B001", 1), NoteLine(2, "07", "B001", 1))).IsSuccess);
        Assert.False(_generator.Generate(Data(NoteLine(1, "07", "BC01", 1), NoteLine(2, "07", "BC01", 1))).IsSuccess);
    }

    public static TheoryData<string, SummaryData> InvalidNotes() => new()
    {
        { "note without reference", Data(NoteLine(1, "07", "BC01", 1) with { ReferencedSeries = null, ReferencedNumber = null, ReferencedDocumentTypeCode = null }) },
        { "note of an invoice", Data(NoteLine(1, "07", "BC01", 1, "F001", 1, "01")) },
        { "reference series", Data(NoteLine(1, "07", "BC01", 1, "X001")) },
        { "reference number", Data(NoteLine(1, "07", "BC01", 1, "B001", 0)) },
        { "receipt with a reference", Data(Taxed(1, "B001", 1) with { ReferencedSeries = "B001", ReferencedNumber = 1, ReferencedDocumentTypeCode = "03" }) },
        { "invoice line", Data(Taxed(1, "B001", 1) with { DocumentTypeCode = "01" }) },
        { "note series", Data(NoteLine(1, "07", "XC01", 1)) },
    };

    [Theory]
    [MemberData(nameof(InvalidNotes))]
    public void Invalid_note_lines_are_refused(string name, SummaryData data)
    {
        var result = _generator.Generate(data);

        Assert.False(result.IsSuccess, name);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, result.Error.Code);
    }

    // ---------- mandatory tags of the official workbook ----------

    private static readonly Regex Comment = new(@"\s*\(.*$", RegexOptions.Singleline);

    private static readonly string[] ConditionalBranches =
    [
        "/sac:SUNATPerceptionSummaryDocumentReference", "/cac:BillingReference", "/cac:AllowanceCharge",
    ];

    private static IEnumerable<string> MandatoryPaths()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.OpenRead(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx"));
        using var reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            if (reader.Name != "Resumen Diario1_1")
            {
                continue;
            }

            while (reader.Read())
            {
                var condition = reader.FieldCount > 4 ? reader.GetValue(4)?.ToString()?.Trim() : null;
                var tag = reader.FieldCount > 7 ? reader.GetValue(7)?.ToString()?.Trim() : null;
                if (condition == "M" && tag is not null && tag.StartsWith("/SummaryDocuments/", StringComparison.Ordinal))
                {
                    var path = Comment.Replace(tag, string.Empty);
                    var at = path.IndexOf('@', StringComparison.Ordinal);
                    yield return at >= 0 ? path[..at] : path;
                }
            }

            yield break;
        }
        while (reader.NextResult());
    }

    [Fact]
    public void Every_mandatory_tag_of_the_workbook_is_present()
    {
        var xml = XDocument.Parse(_generator.Generate(Data()).Value.Xml);
        var paths = MandatoryPaths().Distinct().Where(p => !ConditionalBranches.Any(b => p.Contains(b, StringComparison.Ordinal))).ToList();
        Assert.True(paths.Count >= 15, $"Expected the workbook to list mandatory tags, found {paths.Count}.");

        var missing = paths
            .Select(p => p.Replace("/SummaryDocuments", "/sum:SummaryDocuments", StringComparison.Ordinal))
            .Where(p => !xml.XPathSelectElements(p, Namespaces).Any())
            .ToList();

        Assert.True(missing.Count == 0, "Missing mandatory tags: " + string.Join("; ", missing));
    }

    [Fact]
    public void The_codes_used_exist_in_the_official_catalogues()
    {
        var catalogs = SecureFact.CatalogImporter.CatalogWorkbookParser
            .Parse(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx")).Catalogs;

        var saleValue = catalogs.Single(c => c.Number == "11").Entries.Select(e => e.Code).ToHashSet();
        var itemStatus = catalogs.Single(c => c.Number == "19").Entries.Select(e => e.Code).ToHashSet();

        Assert.Superset(new HashSet<string> { "01", "02", "03" }, saleValue);
        Assert.Contains("1", itemStatus);
        Assert.Equal("Adicionar", catalogs.Single(c => c.Number == "19").Entries.Single(e => e.Code == "1").Description);
    }

    // ---------- signing ----------

    [Fact]
    public void A_summary_can_be_signed_and_stays_valid_against_the_schema()
    {
        using var certificate = SigningTests.NewCertificate();
        var signed = new XmlDsigSigner().Sign(_generator.Generate(Data()).Value.Xml, certificate);

        Assert.True(signed.IsSuccess, signed.IsSuccess ? null : signed.Error.Detail);
        Assert.True(new XmlDsigSigner().Verify(signed.Value.Xml).Value.IsValid);
        Assert.Empty(SchemaErrors(XDocument.Parse(signed.Value.Xml), addDummySignature: false));
    }

    // ---------- refusals ----------

    [Fact]
    public void A_receipt_over_700_soles_needs_the_buyer_identified()
    {
        var anonymous = Exempt(1, "B001", 1) with { TotalAmount = 800m, ExemptAmount = 800m };

        var result = _generator.Generate(Data(anonymous));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, result.Error.Code);
        Assert.True(_generator.Generate(Data(anonymous with { BuyerDocumentTypeCode = "1", BuyerDocumentNumber = "12345678" })).IsSuccess);
    }

    public static TheoryData<string, SummaryData, string> InvalidData() => new()
    {
        { "no lines", Data() with { Lines = [] }, ErrorCodes.CpeInvalidDocument },
        { "bad ruc", Data() with { Ruc = "123" }, ErrorCodes.CpeInvalidDocument },
        { "correlative 0", Data() with { Correlative = 0 }, ErrorCodes.CpeInvalidDocument },
        { "correlative 6 digits", Data() with { Correlative = 100000 }, ErrorCodes.CpeInvalidDocument },
        { "generated before issued", Data() with { IssueDate = new DateOnly(2026, 9, 29) }, ErrorCodes.CpeInvalidDocument },
        { "series not B", Data(Taxed(1, "F001", 1)), ErrorCodes.CpeInvalidDocument },
        { "number zero", Data(Taxed(1, "B001", 0)), ErrorCodes.CpeInvalidDocument },
        { "number 9 digits", Data(Taxed(1, "B001", 100_000_000)), ErrorCodes.CpeInvalidDocument },
        { "duplicate", Data(Taxed(1, "B001", 1), Taxed(2, "B001", 1)), ErrorCodes.CpeInvalidDocument },
        { "line numbers", Data(Taxed(2, "B001", 1)), ErrorCodes.CpeInvalidDocument },
        { "negative", Data(Taxed(1, "B001", 1) with { IgvAmount = -1m }), ErrorCodes.CpeInvalidDocument },
        { "only buyer type", Data(Taxed(1, "B001", 1) with { BuyerDocumentNumber = null }), ErrorCodes.CpeInvalidDocument },
        { "rate as percent", Data(Taxed(1, "B001", 1) with { IgvRate = 18m }), ErrorCodes.CpeInvalidDocument },
        { "free only", Data(Taxed(1, "B001", 1) with { TaxedAmount = 0m, TotalAmount = 18m }), ErrorCodes.CpeUnsupported },
        { "total mismatch", Data(Taxed(1, "B001", 1) with { TotalAmount = 200m }), ErrorCodes.CpeUnsupported },
    };

    [Theory]
    [MemberData(nameof(InvalidData))]
    public void Invalid_or_unsupported_data_is_refused_not_emitted(string name, SummaryData data, string code)
    {
        var result = _generator.Generate(data);

        Assert.False(result.IsSuccess, name);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void A_summary_is_limited_to_500_lines()
    {
        SummaryLineData[] Lines(int count) => Enumerable.Range(1, count).Select(i => Taxed(i, "B001", i)).ToArray();

        Assert.True(_generator.Generate(Data(Lines(500))).IsSuccess);
        Assert.False(_generator.Generate(Data(Lines(501))).IsSuccess);
    }

    [Fact]
    public void Text_is_escaped_so_the_document_stays_well_formed()
    {
        var data = Data() with { LegalName = "EMISORA <DEMO> & \"HIJOS\" SAC" };

        var xml = XDocument.Parse(_generator.Generate(data).Value.Xml);

        Assert.Equal("EMISORA <DEMO> & \"HIJOS\" SAC", xml.XPathSelectElement("//cbc:RegistrationName", Namespaces)!.Value);
    }
}
