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
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Detraction = detracted.Detraction! with { GoodsOrServiceCode = "027" } }).Error.Code); // transport goes with 1004
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(detracted with { Retention = new UblRetention(3m, 236m, 7.08m) }).Error.Code); // not both
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(retained with { Retention = retained.Retention! with { Amount = 20m } }).Error.Code); // amount is base x percentage
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(retained with { Retention = retained.Retention! with { BaseAmount = 300m, Amount = 9m } }).Error.Code); // base above the total
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(Data("03", ("10", 1m, 1000m)) with { Retention = new UblRetention(3m, 1180m, 35.4m) }).Error.Code); // receipts do not
    }

    // ---------- legends of the exonerated sales ----------

    [Theory]
    [InlineData("01", "2001", "BIENES TRANSFERIDOS EN LA AMAZONÍA REGIÓN SELVA PARA SER CONSUMIDOS EN LA MISMA")]
    [InlineData("03", "2002", "SERVICIOS PRESTADOS EN LA AMAZONÍA REGIÓN SELVA PARA SER CONSUMIDOS EN LA MISMA")]
    [InlineData("01", "2003", "CONTRATOS DE CONSTRUCCIÓN EJECUTADOS EN LA AMAZONÍA REGIÓN SELVA")]
    [InlineData("01", "2008", "VENTA EXONERADA DEL IGV-ISC-IPM. PROHIBIDA LA VENTA FUERA DE LA ZONA COMERCIAL DE TACNA")]
    [InlineData("03", "2008", "VENTA EXONERADA DEL IGV-ISC-IPM. PROHIBIDA LA VENTA FUERA DE LA ZONA COMERCIAL DE TACNA")]
    public void An_exonerated_sale_states_its_legend_with_the_catalogue_text(string type, string code, string text)
    {
        var result = _generator.GenerateInvoice(Data(type, ("20", 1m, 100m)) with { LegendCodes = [code] });

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var note = Assert.Single(xml.XPathSelectElements("/inv:Invoice/cbc:Note", Namespaces));
        Assert.Equal(code, note.Attribute("languageLocaleID")!.Value);
        Assert.Equal(text, note.Value);
    }

    [Fact]
    public void Several_legends_of_the_exonerated_sales_go_together_with_a_mixed_document()
    {
        var result = _generator.GenerateInvoice(Data("01", ("20", 1m, 100m), ("10", 1m, 100m)) with { LegendCodes = ["2001", "2008"] });

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal(["2001", "2008"], xml.XPathSelectElements("/inv:Invoice/cbc:Note", Namespaces).Select(n => n.Attribute("languageLocaleID")!.Value));
    }

    [Fact]
    public void The_legends_of_the_exonerated_sales_need_exonerated_operations_and_catalogue_codes()
    {
        var exempt = Data("01", ("20", 1m, 100m));

        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(Data("01", ("10", 1m, 100m)) with { LegendCodes = ["2008"] }).Error.Code); // rule 3289
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(Data("03", ("30", 1m, 100m)) with { LegendCodes = ["2001"] }).Error.Code); // 4022
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(exempt with { LegendCodes = ["2009"] }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(exempt with { LegendCodes = ["2007"] }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(exempt with { LegendCodes = ["2008", "2008"] }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(exempt with { LegendCodes = [""] }).Error.Code);
        Assert.True(_generator.GenerateInvoice(exempt with { LegendCodes = [] }).IsSuccess);
        Assert.Empty(Parse(_generator.GenerateInvoice(exempt).Value).XPathSelectElements("/inv:Invoice/cbc:Note", Namespaces));
    }

    // ---------- detraction types 1002-1004 ----------

    private static readonly UblFishing Catch = new("CO-10955-PM", "LUANA II", "Anchoveta", "Planta pesquera, Puerto Mollendo", new DateOnly(2026, 9, 28), 185.85m);

    private static readonly UblCargoTransport Trip = new("150101", "Av. Argentina 123, Lima", "040101", "Calle Mercaderes 45, Arequipa", "Transporte de cemento en bolsas", 1500m, 1200m, 1000m);

    private static UblInvoiceData WithLine(UblInvoiceData data, Func<UblLine, UblLine> change) => data with { Lines = data.Lines.Select(change).ToList() };

    private static UblInvoiceData FishingSale() =>
        WithLine(Detracted() with { OperationTypeCode = "1002", Detraction = new UblDetraction("004", 4m, 9m, "00012345678") }, l => l with { Fishing = Catch }); // 236.00 x 4 % = 9.44, deposited as 9

    private static UblInvoiceData PassengerSale() =>
        Detracted() with { OperationTypeCode = "1003", Detraction = new UblDetraction("028", 4m, 9m, "00012345678") };

    private static UblInvoiceData CargoSale() =>
        WithLine(Detracted() with { OperationTypeCode = "1004", Detraction = new UblDetraction("027", 4m, 9m, "00012345678") }, l => l with { Transport = Trip });

    [Fact]
    public void A_fishing_sale_states_the_vessel_species_unloading_and_quantity_of_every_line()
    {
        var result = _generator.GenerateInvoice(FishingSale());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("1002", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        Assert.Equal("004", xml.XPathSelectElement("/inv:Invoice/cac:PaymentTerms[cbc:ID='Detraccion']/cbc:PaymentMeansID", Namespaces)!.Value);
        Assert.Equal("2006", xml.XPathSelectElement("/inv:Invoice/cbc:Note", Namespaces)!.Attribute("languageLocaleID")!.Value);

        var properties = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine/cac:Item/cac:AdditionalItemProperty", Namespaces).ToList();
        Assert.Equal(["3001", "3002", "3003", "3004", "3005", "3006"], properties.Select(p => p.XPathSelectElement("cbc:NameCode", Namespaces)!.Value));
        string Value(string code) => properties.Single(p => p.XPathSelectElement("cbc:NameCode", Namespaces)!.Value == code).XPathSelectElement("cbc:Value", Namespaces)!.Value;
        Assert.Equal("CO-10955-PM", Value("3001"));
        Assert.Equal("LUANA II", Value("3002"));
        Assert.Equal("Anchoveta", Value("3003"));
        Assert.Equal("Planta pesquera, Puerto Mollendo", Value("3004"));
        Assert.Equal("2026-09-28", properties[4].XPathSelectElement("cac:UsabilityPeriod/cbc:StartDate", Namespaces)!.Value);
        var quantity = properties[5].XPathSelectElement("cbc:ValueQuantity", Namespaces)!;
        Assert.Equal("185.85", quantity.Value);
        Assert.Equal("TNE", quantity.Attribute("unitCode")!.Value);
        Assert.All(properties, p => Assert.Equal("PE:SUNAT", p.XPathSelectElement("cbc:NameCode", Namespaces)!.Attribute("listAgencyName")!.Value));
    }

    [Fact]
    public void A_passenger_transport_sale_needs_only_the_operation_type_and_its_code()
    {
        var result = _generator.GenerateInvoice(PassengerSale());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("1003", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        Assert.Equal("028", xml.XPathSelectElement("/inv:Invoice/cac:PaymentTerms[cbc:ID='Detraccion']/cbc:PaymentMeansID", Namespaces)!.Value);
        Assert.Empty(xml.XPathSelectElements("//cac:AdditionalItemProperty", Namespaces));
        Assert.Empty(xml.XPathSelectElements("//cac:Delivery", Namespaces));
    }

    [Fact]
    public void A_cargo_transport_sale_states_origin_destination_trip_and_the_three_reference_values_of_every_line()
    {
        var result = _generator.GenerateInvoice(CargoSale());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("1004", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        Assert.Equal("027", xml.XPathSelectElement("/inv:Invoice/cac:PaymentTerms[cbc:ID='Detraccion']/cbc:PaymentMeansID", Namespaces)!.Value);

        var delivery = xml.XPathSelectElement("/inv:Invoice/cac:InvoiceLine/cac:Delivery", Namespaces)!;
        var origin = delivery.XPathSelectElement("cac:Despatch/cac:DespatchAddress", Namespaces)!;
        Assert.Equal("150101", origin.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("PE:INEI", origin.XPathSelectElement("cbc:ID", Namespaces)!.Attribute("schemeAgencyName")!.Value);
        Assert.Equal("Ubigeos", origin.XPathSelectElement("cbc:ID", Namespaces)!.Attribute("schemeName")!.Value);
        Assert.Equal("Av. Argentina 123, Lima", origin.XPathSelectElement("cac:AddressLine/cbc:Line", Namespaces)!.Value);
        Assert.Equal("Transporte de cemento en bolsas", delivery.XPathSelectElement("cac:Despatch/cbc:Instructions", Namespaces)!.Value);
        var destination = delivery.XPathSelectElement("cac:DeliveryLocation/cac:Address", Namespaces)!;
        Assert.Equal("040101", destination.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("Calle Mercaderes 45, Arequipa", destination.XPathSelectElement("cac:AddressLine/cbc:Line", Namespaces)!.Value);
        var terms = delivery.XPathSelectElements("cac:DeliveryTerms", Namespaces).ToDictionary(t => t.XPathSelectElement("cbc:ID", Namespaces)!.Value, t => t.XPathSelectElement("cbc:Amount", Namespaces)!);
        Assert.Equal(["01", "02", "03"], terms.Keys.Order());
        Assert.Equal(("1500.00", "1200.00", "1000.00"), (terms["01"].Value, terms["02"].Value, terms["03"].Value));
        Assert.All(terms.Values, a => Assert.Equal("PEN", a.Attribute("currencyID")!.Value));
        Assert.Empty(xml.XPathSelectElements("//cac:AdditionalItemProperty", Namespaces));
    }

    private static readonly UblTransportLeg FirstLeg = new("150101", "020801", "C3", 15m, "TRAMO LIMA-CASMA", 12m, 1232.28m, 1078.25m);

    private static readonly UblTransportLeg SecondLeg = new("020801", "130101", "C4", 18m, "TRAMO CASMA-TRUJILLO", 12m, 395.64m, 415.42m, true);

    private static UblInvoiceData CargoSaleWithLegs(params UblTransportLeg[] legs) =>
        WithLine(CargoSale(), l => l with { Transport = Trip with { Legs = legs } });

    [Fact]
    public void The_legs_of_a_cargo_transport_state_the_route_and_the_vehicle_of_each_one_inside_the_shipment()
    {
        var result = _generator.GenerateInvoice(CargoSaleWithLegs(FirstLeg, SecondLeg));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        var schemaErrors = SchemaErrors(xml);
        Assert.True(schemaErrors.Count == 0, string.Join(" | ", schemaErrors));
        var shipment = xml.XPathSelectElement("/inv:Invoice/cac:InvoiceLine/cac:Delivery/cac:Shipment", Namespaces)!;
        Assert.Equal("01", shipment.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        var legs = shipment.XPathSelectElements("cac:Consignment", Namespaces).ToList();
        Assert.Equal(2, legs.Count);

        var first = legs[0];
        Assert.Equal("1", first.XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("TRAMO LIMA-CASMA", first.XPathSelectElement("cbc:CarrierServiceInstructions", Namespaces)!.Value);
        Assert.Equal("1078.25", first.XPathSelectElement("cbc:DeclaredForCarriageValueAmount", Namespaces)!.Value);
        Assert.Equal("PEN", first.XPathSelectElement("cbc:DeclaredForCarriageValueAmount", Namespaces)!.Attribute("currencyID")!.Value);
        var origin = first.XPathSelectElement("cac:PlannedPickupTransportEvent/cac:Location/cbc:ID", Namespaces)!;
        Assert.Equal("150101", origin.Value);
        Assert.Equal("PE:INEI", origin.Attribute("schemeAgencyName")!.Value);
        Assert.Equal("Ubigeos", origin.Attribute("schemeName")!.Value);
        Assert.Equal("020801", first.XPathSelectElement("cac:PlannedDeliveryTransportEvent/cac:Location/cbc:ID", Namespaces)!.Value);
        var configuration = first.XPathSelectElement("cac:TransportHandlingUnit/cac:TransportEquipment/cbc:SizeTypeCode", Namespaces)!;
        Assert.Equal("C3", configuration.Value);
        Assert.Equal("PE:MTC", configuration.Attribute("listAgencyName")!.Value);
        Assert.Equal("Configuracion Vehícular", configuration.Attribute("listName")!.Value);
        var loads = first.XPathSelectElements("cac:TransportHandlingUnit/cac:MeasurementDimension", Namespaces)
            .ToDictionary(m => m.XPathSelectElement("cbc:AttributeID", Namespaces)!.Value, m => m.XPathSelectElement("cbc:Measure", Namespaces)!);
        Assert.Equal(("15.00", "12.00"), (loads["01"].Value, loads["02"].Value));
        Assert.All(loads.Values, m => Assert.Equal("TNE", m.Attribute("unitCode")!.Value));
        Assert.Equal("1232.28", first.XPathSelectElement("cac:DeliveryTerms/cbc:Amount", Namespaces)!.Value);
        Assert.Empty(first.XPathSelectElements("cac:TransportHandlingUnit/cac:TransportEquipment/cbc:ReturnabilityIndicator", Namespaces));

        Assert.Equal("2", legs[1].XPathSelectElement("cbc:ID", Namespaces)!.Value);
        Assert.Equal("true", legs[1].XPathSelectElement("cac:TransportHandlingUnit/cac:TransportEquipment/cbc:ReturnabilityIndicator", Namespaces)!.Value);
    }

    [Fact]
    public void A_leg_with_only_its_required_data_states_no_optional_nodes()
    {
        var result = _generator.GenerateInvoice(CargoSaleWithLegs(new UblTransportLeg("150101", "130101", "T3S3", 25m)));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var leg = xml.XPathSelectElement("//cac:Shipment/cac:Consignment", Namespaces)!;
        Assert.Empty(leg.XPathSelectElements("cbc:CarrierServiceInstructions|cbc:DeclaredForCarriageValueAmount|cac:DeliveryTerms", Namespaces));
        Assert.Single(leg.XPathSelectElements("cac:TransportHandlingUnit/cac:MeasurementDimension", Namespaces));
        Assert.Empty(Parse(_generator.GenerateInvoice(CargoSale()).Value).XPathSelectElements("//cac:Shipment", Namespaces)); // no legs, no shipment
    }

    [Fact]
    public void The_legs_of_a_cargo_transport_keep_the_formats_of_the_rules()
    {
        UblTransportLeg[][] refused =
        [
            [FirstLeg with { OriginUbigeo = "1501" }],
            [FirstLeg with { DestinationUbigeo = "02080A" }],
            [FirstLeg with { VehicleConfiguration = " " }],
            [FirstLeg with { VehicleConfiguration = new string('C', 16) }], // 1 to 15 (observation 4273)
            [FirstLeg with { UsefulLoadTonnes = 0m }],
            [FirstLeg with { UsefulLoadTonnes = 1.234m }],
            [FirstLeg with { Description = "ab" }], // 3 to 100 (4271)
            [FirstLeg with { Description = new string('D', 101) }],
            [FirstLeg with { Description = "Lima\nCasma" }],
            [FirstLeg with { EffectiveLoadTonnes = 0m }],
            [FirstLeg with { EffectiveLoadReferenceValue = -1m }], // 4272
            [FirstLeg with { NominalLoadReferenceValue = 0m }], // 4278
            [.. Enumerable.Repeat(FirstLeg, 100)], // the leg identifier has 2 digits
        ];
        foreach (var legs in refused)
        {
            Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(CargoSaleWithLegs(legs)).Error.Code);
        }

        Assert.True(_generator.GenerateInvoice(CargoSaleWithLegs([.. Enumerable.Repeat(FirstLeg, 99)])).IsSuccess);
        Assert.True(_generator.GenerateInvoice(CargoSaleWithLegs(FirstLeg with { VehicleConfiguration = new string('C', 15), Description = new string('D', 100) })).IsSuccess);
    }

    [Fact]
    public void The_detraction_types_demand_their_code_and_their_line_data()
    {
        var fishing = FishingSale();
        var passenger = PassengerSale();
        var cargo = CargoSale();

        // Rule 3129: the code of the detraction follows the operation type, and 1001 takes none of the three.
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(fishing with { Detraction = fishing.Detraction! with { GoodsOrServiceCode = "037" } }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(passenger with { Detraction = passenger.Detraction! with { GoodsOrServiceCode = "027" } }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(cargo with { Detraction = cargo.Detraction! with { GoodsOrServiceCode = "028" } }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(Detracted() with { Detraction = new UblDetraction("004", 4m, 9m, "00012345678") }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(fishing with { Detraction = null }).Error.Code);

        // Rules 3063, 3130–3135, 3116–3126: every line carries the data of its type, and only that type does.
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(fishing, l => l with { Fishing = null })).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(cargo, l => l with { Transport = null })).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(Detracted(), l => l with { Fishing = Catch })).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(Detracted(), l => l with { Transport = Trip })).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(passenger, l => l with { Transport = Trip })).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(fishing, l => l with { Transport = Trip })).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(WithLine(Data("01", ("10", 2m, 100m)), l => l with { Fishing = Catch })).Error.Code);
    }

    [Fact]
    public void The_line_data_of_a_fishing_or_cargo_transport_sale_keeps_the_formats_of_the_rules()
    {
        UblInvoiceData Fishing(Func<UblFishing, UblFishing> change) => WithLine(FishingSale(), l => l with { Fishing = change(Catch) });
        UblInvoiceData Cargo(Func<UblCargoTransport, UblCargoTransport> change) => WithLine(CargoSale(), l => l with { Transport = change(Trip) });

        UblInvoiceData[] refused =
        [
            Fishing(f => f with { VesselRegistration = new string('A', 16) }), // 3001: up to 15 characters
            Fishing(f => f with { VesselRegistration = " " }),
            Fishing(f => f with { VesselName = new string('A', 101) }), // 3002: up to 100
            Fishing(f => f with { SpeciesType = new string('A', 151) }), // 3003: up to 150
            Fishing(f => f with { UnloadingPlace = new string('A', 101) }), // 3004: up to 100
            Fishing(f => f with { SpeciesType = "Anchoveta\ncongelada" }), // no line breaks
            Fishing(f => f with { SpeciesQuantity = 0m }),
            Fishing(f => f with { SpeciesQuantity = 1.234m }),
            Cargo(t => t with { OriginUbigeo = "1501" }),
            Cargo(t => t with { DestinationUbigeo = "04010A" }),
            Cargo(t => t with { OriginAddress = "ab" }), // 3 to 200 characters
            Cargo(t => t with { DestinationAddress = new string('A', 201) }),
            Cargo(t => t with { TripDetail = "ab" }), // 3 to 500
            Cargo(t => t with { TripDetail = new string('A', 501) }),
            Cargo(t => t with { TripDetail = "Lima\tArequipa" }),
            Cargo(t => t with { ServiceReferenceValue = 0m }),
            Cargo(t => t with { EffectiveLoadReferenceValue = -1m }),
            Cargo(t => t with { NominalLoadReferenceValue = 10.001m }),
        ];
        foreach (var data in refused)
        {
            Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data).Error.Code);
        }

        var edge = WithLine(FishingSale(), l => l with { Fishing = Catch with { VesselRegistration = new string('A', 15), VesselName = new string('B', 100), SpeciesType = new string('C', 150), UnloadingPlace = new string('D', 100) } });
        Assert.True(_generator.GenerateInvoice(edge).IsSuccess);
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
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0202")).Error.Code); // lodging and tourist packages need the guest in every line
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0205")).Error.Code);
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData("0209")).Error.Code);
    }

    // ---------- lodging and tourist package exports ----------

    private static readonly UblGuest TouristGuest = new("John Smith", "7", "X1234567", "US");

    private static readonly UblGuest LodgedGuest = new("John Smith", "7", "X1234567", "US", "CA", new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 28), 3);

    private static UblInvoiceData Lodging(UblGuest? guest = null) =>
        WithLine(ExportData("0202"), l => l with { Guest = guest ?? LodgedGuest });

    private static UblInvoiceData Package(UblGuest? guest = null) =>
        WithLine(ExportData("0205"), l => l with { Guest = guest ?? TouristGuest });

    [Fact]
    public void A_lodging_export_states_the_guest_and_the_stay_of_every_line_with_the_catalogue_55_concepts()
    {
        var result = _generator.GenerateInvoice(Lodging());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("0202", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        var properties = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine/cac:Item/cac:AdditionalItemProperty", Namespaces).ToList();
        Assert.Equal(["4000", "4001", "4002", "4003", "4004", "4005", "4006", "4007", "4008", "4009"], properties.Select(p => p.XPathSelectElement("cbc:NameCode", Namespaces)!.Value));
        XElement Property(string code) => properties.Single(p => p.XPathSelectElement("cbc:NameCode", Namespaces)!.Value == code);
        Assert.Equal("US", Property("4000").XPathSelectElement("cbc:Value", Namespaces)!.Value);
        Assert.Equal("CA", Property("4001").XPathSelectElement("cbc:Value", Namespaces)!.Value);
        Assert.Equal("2026-09-25", Property("4002").XPathSelectElement("cac:UsabilityPeriod/cbc:StartDate", Namespaces)!.Value);
        Assert.Equal("2026-09-26", Property("4003").XPathSelectElement("cac:UsabilityPeriod/cbc:StartDate", Namespaces)!.Value);
        Assert.Equal("2026-09-29", Property("4004").XPathSelectElement("cac:UsabilityPeriod/cbc:StartDate", Namespaces)!.Value);
        Assert.Equal("2026-09-28", Property("4006").XPathSelectElement("cac:UsabilityPeriod/cbc:StartDate", Namespaces)!.Value);
        var days = Property("4005").XPathSelectElement("cac:UsabilityPeriod/cbc:DurationMeasure", Namespaces)!;
        Assert.Equal("3", days.Value);
        Assert.Equal("DAY", days.Attribute("unitCode")!.Value);
        Assert.Equal("John Smith", Property("4007").XPathSelectElement("cbc:Value", Namespaces)!.Value);
        Assert.Equal("7", Property("4008").XPathSelectElement("cbc:Value", Namespaces)!.Value);
        Assert.Equal("X1234567", Property("4009").XPathSelectElement("cbc:Value", Namespaces)!.Value);
        Assert.All(properties, p => Assert.Equal("urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo55", p.XPathSelectElement("cbc:NameCode", Namespaces)!.Attribute("listURI")!.Value));
    }

    [Fact]
    public void A_tourist_package_export_states_only_the_guest()
    {
        var result = _generator.GenerateInvoice(Package());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("0205", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        Assert.Equal(["4000", "4007", "4008", "4009"], xml.XPathSelectElements("//cac:AdditionalItemProperty/cbc:NameCode", Namespaces).Select(n => n.Value));
    }

    [Fact]
    public void The_guest_of_a_lodging_or_package_follows_the_rules_of_the_sheet()
    {
        UblInvoiceData[] refused =
        [
            WithLine(ExportData("0202"), l => l), // no guest in a lodging (rules 3136-3145)
            WithLine(ExportData("0205"), l => l),
            WithLine(ExportData("0203"), l => l with { Guest = TouristGuest }), // guest in another type
            WithLine(ExportData(), l => l with { Guest = TouristGuest }),
            Package(LodgedGuest), // a package states no stay
            Lodging(TouristGuest), // a lodging states the stay
            Lodging(LodgedGuest with { CheckOutDate = new DateOnly(2026, 9, 25) }), // observation 4282: out before in
            Lodging(LodgedGuest with { StayDays = 10_000 }),
            Lodging(LodgedGuest with { StayDays = -1 }),
            Lodging(LodgedGuest with { ConsumptionDate = null }),
            Lodging(LodgedGuest with { ResidenceCountryCode = "usa" }),
            Package(TouristGuest with { Name = "ab" }), // 3 to 200
            Package(TouristGuest with { Name = new string('A', 201) }),
            Package(TouristGuest with { DocumentNumber = "ab" }), // 3 to 20
            Package(TouristGuest with { DocumentNumber = new string('1', 21) }),
            Package(TouristGuest with { DocumentTypeCode = "Z" }), // catalogue 06
            Package(TouristGuest with { PassportCountryCode = "U" }),
            Package(TouristGuest with { Name = "John\nSmith" }),
        ];
        foreach (var data in refused)
        {
            Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(data).Error.Code);
        }

        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData("0202", "03") with { Lines = Lodging().Lines }).Error.Code); // invoices only (catalogue 51)
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData("0205", "03") with { Lines = Package().Lines }).Error.Code);
        Assert.True(_generator.GenerateInvoice(Lodging(LodgedGuest with { CheckOutDate = LodgedGuest.CheckInDate, StayDays = 0 })).IsSuccess);
    }

    // ---------- export of services ----------

    [Theory]
    [InlineData("0203")]
    [InlineData("0204")]
    [InlineData("0206")]
    [InlineData("0207")]
    public void An_export_of_services_without_a_country_validates_against_the_schema_and_states_its_type(string operation)
    {
        var result = _generator.GenerateInvoice(ExportData(operation));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal(operation, xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        Assert.Equal("9995", xml.XPathSelectElement("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Empty(xml.XPathSelectElements("/inv:Invoice/cac:Delivery", Namespaces));
    }

    [Theory]
    [InlineData("0201")]
    [InlineData("0208")]
    public void A_service_used_abroad_states_the_country_in_the_delivery_location(string operation)
    {
        var result = _generator.GenerateInvoice(ExportData(operation) with { UsageCountryCode = "US" });

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var country = xml.XPathSelectElement("/inv:Invoice/cac:Delivery/cac:DeliveryLocation/cac:Address/cac:Country/cbc:IdentificationCode", Namespaces)!;
        Assert.Equal("US", country.Value);
        Assert.Equal("ISO 3166-1", country.Attribute("listID")!.Value);
        Assert.Equal("Country", country.Attribute("listName")!.Value);
        var order = xml.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(order.IndexOf("AccountingCustomerParty") < order.IndexOf("Delivery") && order.IndexOf("Delivery") < order.IndexOf("TaxTotal"));
    }

    [Fact]
    public void The_country_of_use_follows_the_rules_of_its_operation_types()
    {
        var services = ExportData("0201");

        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(services).Error.Code); // 3098: required
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0208")).Error.Code);
        foreach (var country in new[] { "PE", "us", "USA", "U1", "" })
        {
            Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(services with { UsageCountryCode = country }).Error.Code); // 3099: not Peru, a code
        }

        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0203") with { UsageCountryCode = "US" }).Error.Code); // only 0201 and 0208
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData() with { UsageCountryCode = "US" }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0207", affectation: "10")).Error.Code); // 2642: lines are 40
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0203") with { Retention = new UblRetention(3m, 100m, 3m) }).Error.Code);
    }

    [Theory]
    [InlineData("0200", null)]
    [InlineData("0201", "US")]
    [InlineData("0203", null)]
    [InlineData("0204", null)]
    [InlineData("0206", null)]
    [InlineData("0207", null)]
    [InlineData("0208", "CL")]
    public void An_export_receipt_validates_against_the_schema_and_states_its_type(string operation, string? country)
    {
        var result = _generator.GenerateInvoice(ExportData(operation, "03") with { UsageCountryCode = country });

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        Assert.Equal("03", xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Value);
        Assert.Equal(operation, xml.XPathSelectElement("/inv:Invoice/cbc:InvoiceTypeCode", Namespaces)!.Attribute("listID")!.Value);
        Assert.Equal("9995", xml.XPathSelectElement("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Empty(xml.XPathSelectElements("/inv:Invoice/cac:PaymentTerms", Namespaces)); // a receipt states no payment form
    }

    [Fact]
    public void An_export_receipt_follows_the_rules_of_its_type()
    {
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0201", "03")).Error.Code); // 3098: the country
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(ExportData("0200", "03", "10")).Error.Code); // 2642
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData("0202", "03") with { Lines = Lodging().Lines }).Error.Code); // catalogue 51: invoices only
        Assert.Equal(ErrorCodes.CpeUnsupported, _generator.GenerateInvoice(ExportData("0205", "03") with { Lines = Package().Lines }).Error.Code);
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

    private static readonly TaxRates BagRates = new(0.18m, IcbperUnitAmount: 0.50m);

    /// <summary>An invoice with one line of three units that carries the ISC, the plastic bag tax, or both.</summary>
    private static UblInvoiceData SpecialTaxes(IscInput? isc, int bags, string type = "01")
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(3, 100m, "10", Isc: isc, PlasticBagCount: bags)], BagRates)).Value;
        return Data(type, ("10", 3m, 100m)) with
        {
            Lines = [new UblLine(1, "Producto con impuestos", "NIU", "P001", 3, 100m, null, "10", Isc: isc, PlasticBagCount: bags)],
            Totals = totals,
            IcbperUnitAmount = bags > 0 ? 0.50m : 0m,
        };
    }

    [Theory]
    [InlineData("01")]
    [InlineData("03")]
    public void An_isc_line_states_the_base_the_rate_and_the_system_and_adds_to_the_igv_base(string type)
    {
        var data = SpecialTaxes(new IscInput(IscSystem.AdValorem, 0.10m), 0, type);

        var result = _generator.GenerateInvoice(data);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var xml = Parse(result.Value);
        Assert.Empty(SchemaErrors(xml));
        var subtotals = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal", Namespaces).ToList();
        string Text(XElement e, string path) => e.XPathSelectElement(path, Namespaces)!.Value;

        // 300 of value, ISC 30 (10 %), IGV 18 % of 330 = 59.40; the line adds up the three: 89.40.
        Assert.Equal("89.40", xml.XPathSelectElement("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cbc:TaxAmount", Namespaces)!.Value);
        Assert.Equal(["1000", "2000"], subtotals.Select(s => Text(s, "cac:TaxCategory/cac:TaxScheme/cbc:ID")));
        Assert.Equal("330.00", Text(subtotals[0], "cbc:TaxableAmount"));
        Assert.Equal("59.40", Text(subtotals[0], "cbc:TaxAmount"));
        Assert.Equal("300.00", Text(subtotals[1], "cbc:TaxableAmount"));
        Assert.Equal("30.00", Text(subtotals[1], "cbc:TaxAmount"));
        Assert.Equal("10.00", Text(subtotals[1], "cac:TaxCategory/cbc:Percent"));
        Assert.Equal("01", Text(subtotals[1], "cac:TaxCategory/cbc:TierRange"));
        Assert.Equal("ISC", Text(subtotals[1], "cac:TaxCategory/cac:TaxScheme/cbc:Name"));
        Assert.Equal("EXC", Text(subtotals[1], "cac:TaxCategory/cac:TaxScheme/cbc:TaxTypeCode"));
        Assert.Null(subtotals[1].XPathSelectElement("cac:TaxCategory/cbc:TaxExemptionReasonCode", Namespaces));

        // Globally: the IGV base is the value only (rule 3277), the ISC has its base and amount, the total tax is 89.40.
        var global = xml.XPathSelectElements("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal", Namespaces).ToList();
        Assert.Equal("89.40", xml.XPathSelectElement("/inv:Invoice/cac:TaxTotal/cbc:TaxAmount", Namespaces)!.Value);
        Assert.Equal("300.00", Text(global[0], "cbc:TaxableAmount"));
        Assert.Equal("300.00", Text(global[1], "cbc:TaxableAmount"));
        Assert.Equal("30.00", Text(global[1], "cbc:TaxAmount"));
        Assert.Equal("389.40", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
    }

    [Fact]
    public void A_fixed_amount_isc_states_the_system_02_and_the_percent_that_results()
    {
        var data = SpecialTaxes(new IscInput(IscSystem.FixedAmount, 1.50m), 0);

        var xml = Parse(_generator.GenerateInvoice(data).Value);

        Assert.Empty(SchemaErrors(xml));
        var isc = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal", Namespaces).Last();
        Assert.Equal("4.50", isc.XPathSelectElement("cbc:TaxAmount", Namespaces)!.Value);
        Assert.Equal("02", isc.XPathSelectElement("cac:TaxCategory/cbc:TierRange", Namespaces)!.Value);
        Assert.Equal("1.50", isc.XPathSelectElement("cac:TaxCategory/cbc:Percent", Namespaces)!.Value);
    }

    [Fact]
    public void Plastic_bags_state_their_count_the_amount_per_bag_and_no_base()
    {
        var data = SpecialTaxes(null, 3);

        var xml = Parse(_generator.GenerateInvoice(data).Value);

        Assert.Empty(SchemaErrors(xml));
        var bags = xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal", Namespaces).Last();
        Assert.Equal("1.50", bags.XPathSelectElement("cbc:TaxAmount", Namespaces)!.Value);
        Assert.Null(bags.XPathSelectElement("cbc:TaxableAmount", Namespaces));
        Assert.Equal("3", bags.XPathSelectElement("cbc:BaseUnitMeasure", Namespaces)!.Value);
        Assert.Equal("NIU", Xml(xml, "/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal/cbc:BaseUnitMeasure/@unitCode"));
        Assert.Equal("0.50", bags.XPathSelectElement("cac:TaxCategory/cbc:PerUnitAmount", Namespaces)!.Value);
        Assert.Null(bags.XPathSelectElement("cac:TaxCategory/cbc:Percent", Namespaces));
        Assert.Equal("7152", bags.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces)!.Value);
        Assert.Equal("ICBPER", bags.XPathSelectElement("cac:TaxCategory/cac:TaxScheme/cbc:Name", Namespaces)!.Value);

        // The tax stays out of the IGV base and in the total: 300 + 54 IGV + 1.50.
        var global = xml.XPathSelectElements("/inv:Invoice/cac:TaxTotal/cac:TaxSubtotal", Namespaces).Last();
        Assert.Null(global.XPathSelectElement("cbc:TaxableAmount", Namespaces));
        Assert.Equal("1.50", global.XPathSelectElement("cbc:TaxAmount", Namespaces)!.Value);
        Assert.Equal("355.50", xml.XPathSelectElement("/inv:Invoice/cac:LegalMonetaryTotal/cbc:PayableAmount", Namespaces)!.Value);
    }

    [Fact]
    public void The_isc_and_the_plastic_bags_can_share_a_line()
    {
        var xml = Parse(_generator.GenerateInvoice(SpecialTaxes(new IscInput(IscSystem.AdValorem, 0.10m), 3)).Value);

        Assert.Empty(SchemaErrors(xml));
        Assert.Equal(["1000", "2000", "7152"], xml.XPathSelectElements("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cac:TaxScheme/cbc:ID", Namespaces).Select(e => e.Value));
        Assert.Equal("90.90", xml.XPathSelectElement("/inv:Invoice/cac:InvoiceLine/cac:TaxTotal/cbc:TaxAmount", Namespaces)!.Value);
    }

    [Fact]
    public void Special_taxes_that_do_not_match_the_lines_are_refused()
    {
        var withBags = SpecialTaxes(null, 3);
        var withIsc = SpecialTaxes(new IscInput(IscSystem.AdValorem, 0.10m), 0);

        // Bags as many as the units of the line (rule 3236), and the amount per bag in force is needed (rule 4237).
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(withBags with { Lines = [withBags.Lines[0] with { PlasticBagCount = 2 }] }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(withBags with { IcbperUnitAmount = 0m }).Error.Code);

        // The line has to say its system and rate, and must not say what the calculation does not have.
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(withIsc with { Lines = [withIsc.Lines[0] with { Isc = null }] }).Error.Code);
        var plain = Data("01", ("10", 1m, 100m));
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(plain with { Lines = [plain.Lines[0] with { Isc = new IscInput(IscSystem.AdValorem, 0.10m) }] }).Error.Code);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(plain with { Lines = [plain.Lines[0] with { PlasticBagCount = 1 }] }).Error.Code);

        // The ICBPER does not exist before 2019-08-01 (rule 2949).
        Assert.Equal(ErrorCodes.CpeInvalidDocument, _generator.GenerateInvoice(withBags with { IssueDate = new DateOnly(2019, 7, 31) }).Error.Code);
        Assert.True(_generator.GenerateInvoice(withBags with { IssueDate = new DateOnly(2019, 8, 1) }).IsSuccess);
    }
}
