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

    // ---------- refusals ----------

    public static TheoryData<string, UblNoteData, string> InvalidData() => new()
    {
        { "invoice type", Note() with { DocumentTypeCode = "01" }, ErrorCodes.CpeInvalidDocument },
        { "credit reason 11", Note("07", "11"), ErrorCodes.CpeUnsupported },
        { "credit reason 13", Note("07", "13"), ErrorCodes.CpeUnsupported },
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
}
