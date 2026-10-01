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

/// <summary>
/// The generated XML is checked against the official UBL 2.1 schemas stored in docs/regulatory/assets (identical to SUNAT's) and against
/// the mandatory tags of the official validation-rules workbook. Acceptance by SUNAT itself can only be proven against its beta service.
/// </summary>
public class UblInvoiceGeneratorTests
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";

    private static readonly TaxRates Rates = new(0.18m);
    private readonly UblInvoiceGenerator _generator = new();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureFact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static UblInvoiceData Data(string type, params (string Code, decimal Qty, decimal Unit)[] lines)
    {
        var taxLines = lines.Select(l => l.Code is "11" or "21"
            ? new TaxableLine(l.Qty, 0m, l.Code, ReferenceUnitValue: l.Unit)
            : new TaxableLine(l.Qty, l.Unit, l.Code)).ToList();
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest(taxLines, Rates)).Value;
        var ublLines = lines.Select((l, i) => new UblLine(
            i + 1, $"Producto {i + 1}", "NIU", $"P{i + 1:000}", l.Qty, l.Code is "11" or "21" ? 0m : l.Unit, l.Code is "11" or "21" ? l.Unit : null, l.Code)).ToList();
        return new UblInvoiceData(
            type, type == "01" ? "F001" : "B001", 123, new DateOnly(2026, 9, 30), new TimeOnly(13, 25, 51), "PEN", "0101",
            new UblParty("6", "20100066603", "EMISORA DEMO SAC", "Emisora Demo"),
            type == "01" ? new UblParty("6", "20100070970", "CLIENTE DEMO SAC") : new UblParty("1", "12345678", "JUAN PEREZ"),
            ublLines, totals, 0.18m);
    }

    private static XDocument Parse(UblDocument document) => XDocument.Parse(document.Xml);

    /// <summary>Validates against the official UBL 2.1 Invoice schema. A dummy signature stands in for the one the signer adds later.</summary>
    private static List<string> SchemaErrors(XDocument document, bool addDummySignature = true)
    {
        var withSignature = new XDocument(document);
        if (addDummySignature)
        {
            withSignature.Descendants(Ext + "ExtensionContent").First().Add(
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

        var maindoc = Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.1", "maindoc", "UBL-Invoice-2.1.xsd");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add("urn:oasis:names:specification:ubl:schema:xsd:Invoice-2", maindoc);
        // The official xmldsig schema carries an internal DTD; parsing it is safe here because it is a local, versioned file.
        using var dsig = XmlReader.Create(
            Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.1", "common", "UBL-xmldsig-core-schema-2.1.xsd"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);

        var errors = new List<string>();
        withSignature.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    [Fact]
    public void An_invoice_validates_against_the_official_ubl_schema()
    {
        var result = _generator.GenerateInvoice(Data("01", ("10", 2m, 100m)));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(SchemaErrors(Parse(result.Value)));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("03")]
    public void Every_supported_operation_type_validates_against_the_schema(string type)
    {
        var data = Data(type, ("10", 3m, 33.3333333333m), ("20", 1m, 50m), ("30", 2m, 10m), ("11", 1m, 30m), ("21", 4m, 5m));

        var result = _generator.GenerateInvoice(data);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(SchemaErrors(Parse(result.Value)));
    }

    [Fact]
    public void A_really_signed_invoice_validates_against_the_official_schemas()
    {
        using var certificate = SigningTests.NewCertificate();
        var signed = new XmlDsigSigner().Sign(SigningTests.UnsignedInvoice(), certificate).Value.Xml;

        Assert.Empty(SchemaErrors(XDocument.Parse(signed), addDummySignature: false));
    }

    [Fact]
    public void File_names_follow_the_sunat_convention()
    {
        var document = _generator.GenerateInvoice(Data("01", ("10", 1m, 10m))).Value;

        Assert.Equal("20100066603-01-F001-123", document.FileBaseName);
        Assert.Equal("20100066603-01-F001-123.xml", document.XmlFileName);
        Assert.Equal("20100066603-01-F001-123.zip", document.ZipFileName);
    }

    [Fact]
    public void Amounts_in_the_xml_are_exactly_the_calculated_ones()
    {
        var data = Data("01", ("10", 2m, 100m));
        var xml = Parse(_generator.GenerateInvoice(data).Value);

        string Text(string path) => xml.XPathSelectElement(path, Namespaces)!.Value;

        Assert.Equal("236.00", Text("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount"));
        Assert.Equal("200.00", Text("/inv:Invoice/cac:LegalMonetaryTotal/cbc:LineExtensionAmount"));
        Assert.Equal("36.00", Text("/inv:Invoice/cac:TaxTotal/cbc:TaxAmount"));
        Assert.Equal("118.00", Text("/inv:Invoice/cac:InvoiceLine/cac:PricingReference/cac:AlternativeConditionPrice/cbc:PriceAmount"));
        Assert.Equal("18.00", Text("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:Percent"));
        Assert.Equal("S", Text("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:ID"));
        Assert.Equal("1000", Text("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID"));
        Assert.Equal("F001-123", Text("/inv:Invoice/cbc:ID"));
        Assert.Equal("PEN", Xml(xml, "/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount/@currencyID"));
    }

    private static string Xml(XDocument xml, string path) =>
        ((IEnumerable<object>)xml.XPathEvaluate(path, Namespaces)).OfType<XAttribute>().First().Value;

    private static XmlNamespaceManager Namespaces
    {
        get
        {
            var manager = new XmlNamespaceManager(new NameTable());
            manager.AddNamespace("inv", "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2");
            manager.AddNamespace("cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
            manager.AddNamespace("cbc", Cbc.NamespaceName);
            manager.AddNamespace("ext", Ext.NamespaceName);
            return manager;
        }
    }

    [Fact]
    public void Free_operations_use_the_reference_price_type_and_the_free_category()
    {
        var xml = Parse(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m), ("11", 1m, 30m))).Value);

        var freeLine = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine", Namespaces).Last();
        Assert.Equal("02", freeLine.XPathSelectElement("cac:PricingReference/cac:AlternativeConditionPrice/cbc:PriceTypeCode", Namespaces)!.Value);
        Assert.Equal("Z", freeLine.XPathSelectElement("cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:ID", Namespaces)!.Value);
        Assert.Equal("9996", freeLine.XPathSelectElement("cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("0.00", freeLine.XPathSelectElement("cac:Price/cbc:PriceAmount", Namespaces)!.Value);
    }

    [Fact]
    public void Unsupported_scopes_fail_explicitly_instead_of_producing_misleading_xml()
    {
        var withDiscount = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "10", DiscountAffectingBase: 10m)], Rates)).Value;
        var export = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "40")], Rates)).Value;
        var baseData = Data("01", ("10", 1m, 100m));

        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(baseData with { DocumentTypeCode = "07" }).Error.Code);
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(baseData with { Totals = export }).Error.Code);
        var charged = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "10", ChargeNotAffectingBase: 5m)], Rates)).Value;
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(baseData with { Totals = charged }).Error.Code);
        Assert.NotNull(withDiscount);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(baseData with { Lines = [] }).Error.Code);
    }

    [Fact]
    public void Text_content_is_escaped_so_the_document_stays_well_formed()
    {
        var data = Data("01", ("10", 1m, 10m));
        data = data with { Lines = [data.Lines[0] with { Description = "Cable <USB> & \"adaptador\" ñandú" }] };

        var document = _generator.GenerateInvoice(data).Value;

        Assert.Equal("Cable <USB> & \"adaptador\" ñandú", Parse(document).XPathSelectElement("//cbc:Description", Namespaces)!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", document.Xml, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- mandatory tags of the official workbook ----------

    private static readonly Regex Comment = new(@"\s*\(.*$", RegexOptions.Singleline);

    private static IEnumerable<string> MandatoryElementPaths(string sheet)
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
                if (condition == "M" && tag is not null && tag.StartsWith("/Invoice", StringComparison.Ordinal))
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

    [Theory]
    [InlineData("01", "Factura2_0")]
    [InlineData("03", "Boleta2_0")]
    public void Every_mandatory_tag_of_the_workbook_is_present(string type, string sheet)
    {
        var xml = Parse(_generator.GenerateInvoice(Data(type, ("10", 2m, 100m))).Value);
        var paths = MandatoryElementPaths(sheet).Distinct().ToList();
        Assert.True(paths.Count >= 20, $"Expected the workbook to list mandatory tags, found {paths.Count}.");

        var missing = paths
            .Select(p => p.Replace("/Invoice", "/inv:Invoice", StringComparison.Ordinal))
            .Where(p => !xml.XPathSelectElements(p, Namespaces).Any())
            .ToList();

        Assert.True(missing.Count == 0, "Missing mandatory tags: " + string.Join("; ", missing));
    }

    [Fact]
    public void Tax_scheme_names_and_codes_match_catalogue_05()
    {
        // Anti-drift: the scheme table of the generator must agree with the official catalogue loaded from the workbook.
        var official = SecureFact.CatalogImporter.CatalogWorkbookParser
            .Parse(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx"))
            .Catalogs.Single(c => c.Number == "05").Entries.ToDictionary(e => e.Code);

        foreach (var (code, scheme) in UblInvoiceGenerator.SupportedSchemes)
        {
            Assert.Equal(scheme.Name, official[code].Extra["Nombre"]);
            Assert.Equal(scheme.TypeCode, official[code].Extra["Código internacional"]);
        }
    }
}
