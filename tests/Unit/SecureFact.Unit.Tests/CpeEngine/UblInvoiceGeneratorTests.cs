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
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(baseData with { Totals = export }).Error.Code); // export lines need operation type 0200
        var rounded = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "10")], Rates, new GlobalAdjustments(PayableRoundingAmount: 0.5m))).Value;
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(baseData with { Totals = rounded }).Error.Code);

        // Totals that say there are discounts or charges the document does not state are never turned into XML.
        var charged = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1, 100m, "10", ChargeNotAffectingBase: 5m)], Rates)).Value;
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(baseData with { Totals = charged }).Error.Code);
        Assert.NotNull(withDiscount);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(baseData with { Lines = [] }).Error.Code);
    }

    // Found against the SUNAT beta service: an exempt line without the rate tag is rejected with 2992 ("el XML no contiene el tag de la tasa del tributo de la línea").
    [Fact]
    public void Every_line_category_states_its_rate_zero_unless_the_line_carries_igv()
    {
        var xml = Parse(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m), ("20", 1m, 50m), ("30", 1m, 10m), ("11", 1m, 30m), ("21", 1m, 5m))).Value);

        var percents = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine", Namespaces)
            .Select(l => l.XPathSelectElement("cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:Percent", Namespaces)!.Value)
            .ToArray();
        Assert.Equal(["18.00", "0.00", "0.00", "18.00", "0.00"], percents);
        Assert.Empty(xml.XPathSelectElements("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:Percent", Namespaces)); // the document totals do not carry it
    }

    // ---------- detraction and withholding ----------

    private static UblInvoiceData Detracted() =>
        Data("01", ("10", 2m, 100m)) with { OperationTypeCode = "1001", Detraction = new UblDetraction("037", 12m, 28m, "00012345678") }; // 236.00 x 12 % = 28.32, deposited as 28

    private static UblInvoiceData Retained() =>
        Data("01", ("10", 2m, 100m)) with { Retention = new UblRetention(3m, 236m, 7.08m) };

    [Fact]
    public void An_invoice_subject_to_detraction_validates_against_the_schema_and_states_account_code_percentage_amount_and_legend()
    {
        var result = _generator.GenerateInvoice(Detracted());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("1001", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        var means = xml.XPathSelectElement("/inv:Invoice/cac:PaymentMeans", Namespaces)!;
        Assert.Equal("Detraccion", means.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("001", means.XPathSelectElement("cbc:PaymentMeansCode", Namespaces)!.Value);
        Assert.Equal("00012345678", means.XPathSelectElement("cac:PayeeFinancialAccount/cbc:ID", Namespaces)!.Value);
        var terms = xml.XPathSelectElements("/inv:Invoice/cac:PaymentTerms", Namespaces).ToList();
        var detraction = Assert.Single(terms, t => t.XPathSelectElement("cbc:ID", Namespaces)!.Value == "Detraccion");
        Assert.Equal("037", detraction.XPathSelectElement("cbc:PaymentMeansID", Namespaces)!.Value);
        Assert.Equal("12.00", detraction.XPathSelectElement("cbc:PaymentPercent", Namespaces)!.Value);
        Assert.Equal("28.00", detraction.XPathSelectElement("cbc:Amount", Namespaces)!.Value);
        Assert.Equal("PEN", detraction.XPathSelectElement("cbc:Amount", Namespaces)!.Attribute("currencyID")!.Value);
        Assert.Contains(terms, t => t.XPathSelectElement("cbc:PaymentMeansID", Namespaces)!.Value == "Contado"); // the payment form is still stated
        Assert.Equal("2006", xml.XPathSelectElement("/inv:Invoice/cbc:Note", Namespaces)!.Attribute("languageLocaleID")!.Value);
        Assert.Equal("236.00", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value); // the detraction does not change the total
    }

    [Fact]
    public void An_igv_withholding_is_a_global_allowance_of_code_62_that_leaves_the_total_alone()
    {
        var result = _generator.GenerateInvoice(Retained());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var allowance = xml.XPathSelectElement("/inv:Invoice/cac:AllowanceCharge", Namespaces)!;
        Assert.Equal("false", allowance.XPathSelectElement("cbc:ChargeIndicator", Namespaces)!.Value);
        Assert.Equal("62", allowance.XPathSelectElement("cbc:AllowanceChargeReasonCode", Namespaces)!.Value);
        Assert.Equal("0.03", allowance.XPathSelectElement("cbc:MultiplierFactorNumeric", Namespaces)!.Value);
        Assert.Equal("7.08", allowance.XPathSelectElement("cbc:Amount", Namespaces)!.Value);
        Assert.Equal("236.00", allowance.XPathSelectElement("cbc:BaseAmount", Namespaces)!.Value);
        Assert.Equal("236.00", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
        Assert.Empty(xml.XPathSelectElements("//cbc:AllowanceTotalAmount", Namespaces)); // rule 3300 counts codes 01, 03 and 63 only
        Assert.Empty(xml.XPathSelectElements("/inv:Invoice/cbc:Note", Namespaces));
    }

    [Fact]
    public void The_net_pending_amount_of_a_credit_sale_leaves_out_the_detraction_and_the_withholding()
    {
        var detracted = Detracted() with { PaymentForm = "Credito", Installments = [new(100m, new DateOnly(2026, 10, 30)), new(108m, new DateOnly(2026, 11, 30))] }; // 236 - 28
        var retained = Retained() with { PaymentForm = "Credito", Installments = [new(228.92m, new DateOnly(2026, 10, 30))] }; // 236 - 7.08

        Assert.True(_generator.GenerateInvoice(detracted).IsSuccess);
        Assert.True(_generator.GenerateInvoice(retained).IsSuccess);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Installments = [new(236m, new DateOnly(2026, 10, 30))] }).Error.Code); // ignores the detraction
        var withInitial = detracted with { InitialPayment = 8m, Installments = [new(100m, new DateOnly(2026, 10, 30)), new(100m, new DateOnly(2026, 11, 30))] }; // 236 - 28 - 8
        Assert.True(_generator.GenerateInvoice(withInitial).IsSuccess);
    }

    [Fact]
    public void Detractions_and_withholdings_that_break_the_rules_are_refused()
    {
        var detracted = Detracted();
        var retained = Retained();

        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { OperationTypeCode = "0101" }).Error.Code); // 1001 and the detraction go together
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Detraction = null }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Currency = "USD" }).Error.Code); // the amount is in soles
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Detraction = detracted.Detraction! with { Amount = 0m } }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Detraction = detracted.Detraction! with { Amount = 300m } }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Detraction = detracted.Detraction! with { AccountNumber = " " } }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Detraction = detracted.Detraction! with { GoodsOrServiceCode = "027" } }).Error.Code); // transport has its own operation type
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Retention = new UblRetention(3m, 236m, 7.08m) }).Error.Code); // not both
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(retained with { Retention = retained.Retention! with { Amount = 20m } }).Error.Code); // amount is base x percentage
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(retained with { Retention = retained.Retention! with { BaseAmount = 300m, Amount = 9m } }).Error.Code); // base above the total
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(Data("03", ("10", 1m, 1000m)) with { Retention = new UblRetention(3m, 1180m, 35.4m) }).Error.Code); // receipts do not
    }

    // ---------- export of goods ----------

    private static UblInvoiceData ExportData(string operation = "0200", string type = "01", string affectation = "40")
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(2m, 50m, affectation)], Rates)).Value;
        return new UblInvoiceData(
            type, type == "01" ? "F001" : "B001", 123, new DateOnly(2026, 9, 30), new TimeOnly(13, 25, 51), "USD", operation,
            new UblParty("6", "20100066603", "EMISORA DEMO SAC", "Emisora Demo"), new UblParty("0", "-", "FOREIGN BUYER LLC"),
            [new UblLine(1, "Bien de exportación", "NIU", "P001", 2m, 50m, null, affectation)], totals, 0.18m);
    }

    [Fact]
    public void An_export_of_goods_validates_against_the_schema_and_states_tax_9995_without_igv()
    {
        var result = _generator.GenerateInvoice(ExportData());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("0200", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        var total = xml.XPathSelectElement("/inv:Invoice/cac:TaxTotal", Namespaces)!;
        Assert.Equal("0.00", total.XPathSelectElement("cbc:TaxAmount", Namespaces)!.Value);
        var subtotal = Assert.Single(total.XPathSelectElements("cac:TaxSubtotal", Namespaces));
        Assert.Equal("9995", subtotal.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("EXP", subtotal.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:Name", Namespaces)!.Value);
        Assert.Equal("FRE", subtotal.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:TaxTypeCode", Namespaces)!.Value);
        Assert.Equal("100.00", subtotal.XPathSelectElement("cbc:TaxableAmount", Namespaces)!.Value);
        Assert.Equal("0.00", subtotal.XPathSelectElement("cbc:TaxAmount", Namespaces)!.Value);
        var line = xml.XPathSelectElement("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory", Namespaces)!;
        Assert.Equal("0.00", line.XPathSelectElement("cbc:Percent", Namespaces)!.Value); // rule 2992: stated, and zero for 9995 (rule 3110)
        Assert.Equal("40", line.XPathSelectElement("cbc:TaxExemptionReasonCode", Namespaces)!.Value);
        Assert.Equal("100.00", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
    }

    [Fact]
    public void Export_and_sale_lines_do_not_mix_and_only_invoices_with_supported_types_are_exported()
    {
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData(affectation: "10")).Error.Code); // 0200 with taxed lines
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0101")).Error.Code); // export lines on a sale
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData(type: "03")).Error.Code); // no export receipts yet
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData("0201")).Error.Code); // services are not supported yet
    }

    // ---------- IVAP (rice) ----------

    private static readonly TaxRates IvapRates = new(0.18m, 0.04m);

    private static UblInvoiceData IvapData(string type = "01", decimal ivapRate = 0.04m)
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1m, 100m, "17")], IvapRates)).Value;
        return new UblInvoiceData(
            type, type == "01" ? "F001" : "B001", 123, new DateOnly(2026, 9, 30), new TimeOnly(13, 25, 51), "PEN", "0101",
            new UblParty("6", "20100066603", "EMISORA DEMO SAC", "Emisora Demo"),
            type == "01" ? new UblParty("6", "20100070970", "CLIENTE DEMO SAC") : new UblParty("1", "12345678", "JUAN PEREZ"),
            [new UblLine(1, "Arroz pilado", "KGM", null, 1m, 100m, null, "17")], totals, 0.18m, IvapRate: ivapRate);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("03")]
    public void An_invoice_or_receipt_taxed_with_the_ivap_validates_against_the_schema_and_states_tax_1016_at_its_rate(string type)
    {
        var result = _generator.GenerateInvoice(IvapData(type));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var line = xml.XPathSelectElement("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal", Namespaces)!;
        Assert.Equal("1016", line.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("IVAP", line.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:Name", Namespaces)!.Value);
        Assert.Equal("4.00", line.XPathSelectElement("cac:TaxCategory/cbc:Percent", Namespaces)!.Value);
        Assert.Equal("17", line.XPathSelectElement("cac:TaxCategory/cbc:TaxExemptionReasonCode", Namespaces)!.Value);
        var total = xml.XPathSelectElement("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal", Namespaces)!;
        Assert.Equal("1016", total.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("100.00", total.XPathSelectElement("cbc:TaxableAmount", Namespaces)!.Value);
        Assert.Equal("4.00", total.XPathSelectElement("cbc:TaxAmount", Namespaces)!.Value);
        Assert.Equal("104.00", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);

        // Observation 4264: a line taxed with the IVAP needs legend 2007.
        var legend = xml.XPathSelectElement("/inv:Invoice/cbc:Note", Namespaces)!;
        Assert.Equal("2007", legend.Attribute("languageLocaleID")!.Value);
        Assert.Equal("Operación sujeta a IVAP", legend.Value);
    }

    [Fact]
    public void An_ivap_line_without_the_ivap_rate_is_refused_and_other_documents_carry_no_legend()
    {
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(IvapData() with { IvapRate = 0m }).Error.Code);
        Assert.Empty(Parse(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m))).Value).XPathSelectElements("//cbc:Note", Namespaces));
    }

    // ---------- sale on credit ----------

    private static readonly UblInstallment[] TwoInstallments = [new(100m, new DateOnly(2026, 10, 30)), new(136m, new DateOnly(2026, 11, 30))];

    private static UblInvoiceData OnCredit(params UblInstallment[] installments) =>
        Data("01", ("10", 2m, 100m)) with { PaymentForm = "Credito", Installments = installments.Length == 0 ? TwoInstallments : installments };

    [Fact]
    public void An_invoice_on_credit_validates_against_the_schema_and_states_the_net_amount_and_each_installment()
    {
        var result = _generator.GenerateInvoice(OnCredit());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));

        var terms = xml.XPathSelectElements("/inv:Invoice/cac:PaymentTerms", Namespaces).ToList();
        Assert.Equal(["Credito", "Cuota001", "Cuota002"], terms.Select(t => t.XPathSelectElement("cbc:PaymentMeansID", Namespaces)!.Value).ToArray());
        Assert.All(terms, t => Assert.Equal("FormaPago", t.XPathSelectElement("cbc:ID", Namespaces)!.Value));
        Assert.Equal(["236.00", "100.00", "136.00"], terms.Select(t => t.XPathSelectElement("cbc:Amount", Namespaces)!.Value).ToArray());
        Assert.All(terms, t => Assert.Equal("PEN", t.XPathSelectElement("cbc:Amount", Namespaces)!.Attribute("currencyID")!.Value));
        Assert.Null(terms[0].XPathSelectElement("cbc:PaymentDueDate", Namespaces));
        Assert.Equal(["2026-10-30", "2026-11-30"], terms.Skip(1).Select(t => t.XPathSelectElement("cbc:PaymentDueDate", Namespaces)!.Value).ToArray());
    }

    [Fact]
    public void Installments_are_numbered_with_three_digits_and_an_invoice_on_credit_may_have_up_to_999()
    {
        var data = Data("01", ("10", 1m, 1000m)); // payable 1180.00
        var installments = Enumerable.Range(1, 998).Select(i => new UblInstallment(1m, new DateOnly(2026, 10, 1).AddDays(i))).ToList();
        installments.Add(new UblInstallment(182m, new DateOnly(2030, 1, 1)));

        var result = _generator.GenerateInvoice(data with { PaymentForm = "Credito", Installments = installments });

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var ids = Parse(result.Value).XPathSelectElements("/inv:Invoice/cac:PaymentTerms/cbc:PaymentMeansID", Namespaces).Select(e => e.Value).ToList();
        Assert.Equal("Cuota999", ids[^1]);
        Assert.Equal("Cuota010", ids[10]);
        var tooMany = installments.Take(998).Append(new UblInstallment(91m, new DateOnly(2030, 1, 1))).Append(new UblInstallment(91m, new DateOnly(2030, 1, 2))).ToList();
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data with { PaymentForm = "Credito", Installments = tooMany }).Error.Code);
    }

    [Fact]
    public void Credit_without_consistent_installments_is_refused()
    {
        var valid = OnCredit();

        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(valid with { Installments = [] }).Error.Code); // credit with no installment
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(valid with { Installments = [new(100m, new DateOnly(2026, 10, 30))] }).Error.Code); // does not add up
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(valid with { Installments = [new(236m, new DateOnly(2026, 9, 30))] }).Error.Code); // due on the issue date
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(valid with { Installments = [new(0m, new DateOnly(2026, 10, 30)), new(236m, new DateOnly(2026, 10, 30))] }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(valid with { PaymentForm = "Contado" }).Error.Code); // cash with installments
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(valid with { PaymentForm = "Otra" }).Error.Code);
        var receipt = Data("03", ("10", 2m, 100m)) with { PaymentForm = "Credito", Installments = TwoInstallments };
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(receipt).Error.Code); // the receipt has no payment form
    }

    [Fact]
    public void An_initial_payment_lowers_the_net_pending_amount_and_is_not_stated_in_the_xml()
    {
        var data = OnCredit(new UblInstallment(100m, new DateOnly(2026, 10, 30)), new UblInstallment(100m, new DateOnly(2026, 11, 30))) with { InitialPayment = 36m };

        var result = _generator.GenerateInvoice(data);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var terms = xml.XPathSelectElements("/inv:Invoice/cac:PaymentTerms", Namespaces).ToList();
        Assert.Equal(["200.00", "100.00", "100.00"], terms.Select(t => t.XPathSelectElement("cbc:Amount", Namespaces)!.Value).ToArray()); // net pending = 236 - 36
        Assert.Equal("236.00", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
    }

    [Fact]
    public void An_initial_payment_that_does_not_fit_the_installments_is_refused()
    {
        var two = new[] { new UblInstallment(100m, new DateOnly(2026, 10, 30)), new UblInstallment(100m, new DateOnly(2026, 11, 30)) };
        var data = OnCredit(two);

        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data with { InitialPayment = 30m }).Error.Code); // 200 + 30 is not 236
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data with { InitialPayment = 236m }).Error.Code); // nothing left to pay later
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data with { InitialPayment = -36m }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(Data("01", ("10", 2m, 100m)) with { InitialPayment = 36m }).Error.Code); // cash sale
    }

    [Fact]
    public void A_receipt_and_a_cash_invoice_state_no_installments()
    {
        var cash = Parse(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m))).Value);

        Assert.Equal(["Contado"], cash.XPathSelectElements("/inv:Invoice/cac:PaymentTerms/cbc:PaymentMeansID", Namespaces).Select(e => e.Value).ToArray());
        Assert.Empty(cash.XPathSelectElements("//cbc:PaymentDueDate", Namespaces));
    }

    // ---------- discounts and charges ----------

    private static readonly UblLine DiscountedLine = new(1, "Producto con ajustes", "NIU", "P001", 2m, 100m, null, "10", DiscountAffectingBase: 20m, ChargeAffectingBase: 5m, DiscountNotAffectingBase: 10m, ChargeNotAffectingBase: 3m);

    /// <summary>A taxed line with the four line adjustments, an exempt line and the four global adjustments.</summary>
    private static UblInvoiceData Discounted(string type = "01")
    {
        var lines = new[]
        {
            new TaxableLine(2m, 100m, "10", DiscountAffectingBase: 20m, ChargeAffectingBase: 5m, DiscountNotAffectingBase: 10m, ChargeNotAffectingBase: 3m),
            new TaxableLine(1m, 50m, "20"),
        };
        var adjustments = new GlobalAdjustments(DiscountAffectingBase: 12m, ChargeAffectingBase: 4m, DiscountNotAffectingBase: 7m, ChargeNotAffectingBase: 2m);
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest(lines, Rates, adjustments)).Value;
        var ubl = new[] { DiscountedLine, new UblLine(2, "Servicio exonerado", "ZZ", null, 1m, 50m, null, "20") };
        return new UblInvoiceData(
            type, type == "01" ? "F001" : "B001", 123, new DateOnly(2026, 9, 30), null, "PEN", "0101",
            new UblParty("6", "20100066603", "EMISORA DEMO SAC"),
            type == "01" ? new UblParty("6", "20100070970", "CLIENTE DEMO SAC") : new UblParty("1", "12345678", "JUAN PEREZ"),
            ubl, totals, 0.18m, Adjustments: adjustments);
    }

    private static decimal Value(XContainer node, string path) =>
        decimal.Parse(node.XPathSelectElement(path, Namespaces)!.Value, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("01")]
    [InlineData("03")]
    public void Line_and_global_discounts_and_charges_validate_against_the_schema(string type)
    {
        var result = _generator.GenerateInvoice(Discounted(type));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(SchemaErrors(Parse(result.Value)));
    }

    [Fact]
    public void Line_allowances_carry_code_indicator_amount_base_and_factor()
    {
        var xml = Parse(_generator.GenerateInvoice(Discounted()).Value);
        var line = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine", Namespaces).First();

        var nodes = line.XPathSelectElements("cac:AllowanceCharge", Namespaces).ToList();
        Assert.Equal(["00", "47", "01", "48"], nodes.Select(n => n.XPathSelectElement("cbc:AllowanceChargeReasonCode", Namespaces)!.Value).ToArray());
        Assert.Equal(["false", "true", "false", "true"], nodes.Select(n => n.XPathSelectElement("cbc:ChargeIndicator", Namespaces)!.Value).ToArray());

        // 00 and 47 are stated over the value before them (2 x 100 = 200); 01 and 48 over the line value (200 - 20 + 5 = 185).
        Assert.Equal("200.00", nodes[0].XPathSelectElement("cbc:BaseAmount", Namespaces)!.Value);
        Assert.Equal("0.10", nodes[0].XPathSelectElement("cbc:MultiplierFactorNumeric", Namespaces)!.Value);
        Assert.Equal("0.025", nodes[1].XPathSelectElement("cbc:MultiplierFactorNumeric", Namespaces)!.Value);
        Assert.Equal("185.00", nodes[2].XPathSelectElement("cbc:BaseAmount", Namespaces)!.Value);
        Assert.Equal("0.05405", nodes[2].XPathSelectElement("cbc:MultiplierFactorNumeric", Namespaces)!.Value);
        Assert.Equal("185.00", nodes[3].XPathSelectElement("cbc:BaseAmount", Namespaces)!.Value);
        Assert.Equal("185.00", line.XPathSelectElement("cbc:LineExtensionAmount", Namespaces)!.Value);
    }

    [Fact]
    public void Global_allowances_state_the_taxed_base_only_for_the_ones_that_affect_it()
    {
        var xml = Parse(_generator.GenerateInvoice(Discounted()).Value);

        var nodes = xml.XPathSelectElements("/inv:Invoice/cac:AllowanceCharge", Namespaces).ToList();
        Assert.Equal(["02", "49", "03", "50"], nodes.Select(n => n.XPathSelectElement("cbc:AllowanceChargeReasonCode", Namespaces)!.Value).ToArray());
        Assert.Equal(["false", "true", "false", "true"], nodes.Select(n => n.XPathSelectElement("cbc:ChargeIndicator", Namespaces)!.Value).ToArray());
        Assert.Equal(["12.00", "4.00", "7.00", "2.00"], nodes.Select(n => n.XPathSelectElement("cbc:Amount", Namespaces)!.Value).ToArray());

        // Taxed base before the global discount and charge: the taxed line, 185.
        Assert.Equal("185.00", nodes[0].XPathSelectElement("cbc:BaseAmount", Namespaces)!.Value);
        Assert.Equal("185.00", nodes[1].XPathSelectElement("cbc:BaseAmount", Namespaces)!.Value);
        Assert.Null(nodes[2].XPathSelectElement("cbc:BaseAmount", Namespaces));
        Assert.Null(nodes[3].XPathSelectElement("cbc:MultiplierFactorNumeric", Namespaces));
    }

    /// <summary>The arithmetic of the sheet rules 3270, 3277, 3278, 3279/3291, 3280, 3290, 3300 and 3301, checked on the generated XML itself with their tolerance of 1.</summary>
    [Fact]
    public void The_xml_satisfies_the_sheet_arithmetic_for_discounts_and_charges()
    {
        var xml = Parse(_generator.GenerateInvoice(Discounted()).Value);
        var lines = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine", Namespaces).ToList();
        var global = xml.XPathSelectElements("/inv:Invoice/cac:AllowanceCharge", Namespaces).ToDictionary(n => n.XPathSelectElement("cbc:AllowanceChargeReasonCode", Namespaces)!.Value, n => Value(n, "cbc:Amount"));
        var taxedLines = Value(lines[0], "cbc:LineExtensionAmount");
        var lineSum = lines.Sum(l => Value(l, "cbc:LineExtensionAmount"));

        // 3277: taxed total = taxed lines - 02 + 49. 3278: total value = all lines - 02 + 49.
        var taxedTotal = Value(xml, "/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal[cac:TaxCategory/cac:TaxScheme/cbc:ID='1000']/cbc:TaxableAmount");
        Assert.InRange(Math.Abs(taxedTotal - (taxedLines - global["02"] + global["49"])), 0m, 1m);
        var monetary = "/inv:Invoice/cac:LegalMonetaryTotal/";
        Assert.InRange(Math.Abs(Value(xml, monetary + "cbc:LineExtensionAmount") - (lineSum - global["02"] + global["49"])), 0m, 1m);

        // 3291: IGV = taxed base x rate. 3279: price total = value + IGV. 3300/3301: the other allowances and charges. 3280: payable.
        var igv = Value(xml, "/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal[cac:TaxCategory/cac:TaxScheme/cbc:ID='1000']/cbc:TaxAmount");
        Assert.InRange(Math.Abs(igv - (taxedTotal * 0.18m)), 0m, 1m);
        Assert.InRange(Math.Abs(Value(xml, monetary + "cbc:TaxInclusiveAmount") - (Value(xml, monetary + "cbc:LineExtensionAmount") + igv)), 0m, 1m);
        Assert.Equal(10m + global["03"], Value(xml, monetary + "cbc:AllowanceTotalAmount"));
        Assert.Equal(3m + global["50"], Value(xml, monetary + "cbc:ChargeTotalAmount"));
        var payable = Value(xml, monetary + "cbc:PayableAmount");
        Assert.InRange(Math.Abs(payable - (Value(xml, monetary + "cbc:TaxInclusiveAmount") + Value(xml, monetary + "cbc:ChargeTotalAmount") - Value(xml, monetary + "cbc:AllowanceTotalAmount"))), 0m, 1m);

        // 3270: unit price with taxes x quantity = line value + line taxes - discount 01 + charge 48. 3290: amount = base x factor.
        var first = lines[0];
        var priced = Value(first, "cac:PricingReference/cac:AlternativeConditionPrice/cbc:PriceAmount") * 2m;
        Assert.InRange(Math.Abs(priced - (Value(first, "cbc:LineExtensionAmount") + Value(first, "cac:TaxTotal/cbc:TaxAmount") - 10m + 3m)), 0m, 1m);
        foreach (var node in lines[0].XPathSelectElements("cac:AllowanceCharge", Namespaces).Concat(xml.XPathSelectElements("/inv:Invoice/cac:AllowanceCharge[cbc:MultiplierFactorNumeric]", Namespaces)))
        {
            Assert.InRange(Math.Abs(Value(node, "cbc:Amount") - (Value(node, "cbc:BaseAmount") * Value(node, "cbc:MultiplierFactorNumeric"))), 0m, 1m);
        }
    }

    [Fact]
    public void Without_discounts_the_xml_has_no_allowance_nodes_or_allowance_totals()
    {
        var xml = Parse(_generator.GenerateInvoice(Data("01", ("10", 2m, 100m))).Value);

        Assert.Empty(xml.XPathSelectElements("//cac:AllowanceCharge", Namespaces));
        Assert.Empty(xml.XPathSelectElements("//cbc:AllowanceTotalAmount | //cbc:ChargeTotalAmount", Namespaces));
    }

    [Fact]
    public void Allowances_that_do_not_agree_with_the_totals_or_have_no_valid_base_are_refused()
    {
        var data = Discounted();

        // The totals were calculated with different line adjustments than the ones given to the generator.
        var tampered = data with { Lines = [DiscountedLine with { DiscountNotAffectingBase = 0m }, data.Lines[1]] };
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(tampered).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data with { Adjustments = null }).Error.Code);

        // A charge that does not touch the base over a line whose value is zero has no base to be stated on.
        var line = new TaxableLine(1m, 100m, "10", DiscountAffectingBase: 100m, ChargeNotAffectingBase: 5m);
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([line], Rates)).Value;
        var emptyBase = Data("01", ("10", 1m, 100m)) with
        {
            Totals = totals,
            Lines = [new UblLine(1, "Producto", "NIU", null, 1m, 100m, null, "10", DiscountAffectingBase: 100m, ChargeNotAffectingBase: 5m)],
        };
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(emptyBase).Error.Code);
    }

    // Found against the SUNAT beta service: invoices need their payment form (error 3244) and attribute values must match the catalogue
    // names the validation rules list (observations 4252, 4255, 4256).
    [Fact]
    public void An_invoice_states_its_payment_form_and_a_receipt_does_not()
    {
        var invoice = Parse(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m))).Value);
        var receipt = Parse(_generator.GenerateInvoice(Data("03", ("10", 1m, 100m))).Value);

        var terms = Assert.Single(invoice.XPathSelectElements("/inv:Invoice/cac:PaymentTerms", Namespaces));
        Assert.Equal("FormaPago", terms.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("Contado", terms.XPathSelectElement("cbc:PaymentMeansID", Namespaces)!.Value);
        Assert.Empty(receipt.XPathSelectElements("/inv:Invoice/cac:PaymentTerms", Namespaces));
        Assert.False(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m)) with { PaymentForm = "Credito" }).IsSuccess);
    }

    [Fact]
    public void Attribute_values_match_the_names_the_validation_rules_expect()
    {
        var xml = Parse(_generator.GenerateInvoice(Data("01", ("10", 1m, 100m))).Value);

        Assert.Equal("Tipo de Documento", (string?)xml.XPathSelectElement("//cbc:InvoiceTypeCode", Namespaces)!.Attribute("listName"));
        Assert.Equal("Afectacion del IGV", (string?)xml.XPathSelectElement("//cbc:TaxExemptionReasonCode", Namespaces)!.Attribute("listName"));
        var scheme = xml.XPathSelectElement("//cac:TaxScheme/cbc:ID", Namespaces)!;
        Assert.Equal("Codigo de tributos", (string?)scheme.Attribute("schemeName"));
        Assert.Equal("PE:SUNAT", (string?)scheme.Attribute("schemeAgencyName"));
        var buyer = xml.XPathSelectElement("//cac:AccountingCustomerParty//cac:PartyIdentification/cbc:ID", Namespaces)!;
        Assert.Equal("Documento de Identidad", (string?)buyer.Attribute("schemeName"));
        Assert.Equal("urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo06", (string?)buyer.Attribute("schemeURI"));
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
