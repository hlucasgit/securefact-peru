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

public class VoidedDocumentsGeneratorTests
{
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XmlNamespaceManager Namespaces = BuildNamespaces();

    private readonly VoidedDocumentsGenerator _generator = new();

    private static XmlNamespaceManager BuildNamespaces()
    {
        var manager = new XmlNamespaceManager(new NameTable());
        manager.AddNamespace("va", "urn:sunat:names:specification:ubl:peru:schema:xsd:VoidedDocuments-1");
        manager.AddNamespace("sac", "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1");
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

    internal static VoidedLineData Line(int line, string type = "01", string series = "F001", long number = 1, string reason = "Error en la emisión") => new(line, type, series, number, reason);

    internal static VoidedData Data(params VoidedLineData[] lines) =>
        new("20100066603", "EMISORA DEMO SAC", new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1), 1, lines.Length > 0 ? lines : [Line(1), Line(2, "07", "FC01", 4)]);

    private static List<string> SchemaErrors(XDocument document)
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

        var root = Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.0");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add("urn:sunat:names:specification:ubl:peru:schema:xsd:VoidedDocuments-1", Path.Combine(root, "maindoc", "UBLPE-VoidedDocuments-1.0.xsd"));
        using var dsig = XmlReader.Create(Path.Combine(root, "common", "xmldsig-core-schema.xsd"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);

        var errors = new List<string>();
        copy.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    [Fact]
    public void A_communication_validates_against_the_official_schema()
    {
        var result = _generator.Generate(Data());

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Empty(SchemaErrors(XDocument.Parse(result.Value.Xml)));
    }

    [Fact]
    public void The_identifier_names_and_lines_follow_the_manual()
    {
        var document = _generator.Generate(Data()).Value;
        var xml = XDocument.Parse(document.Xml);

        Assert.Equal("RA-20261001-1", document.Identifier);
        Assert.Equal("20100066603-RA-20261001-1", document.FileBaseName);
        Assert.Equal("20100066603-RA-20261001-1.zip", document.ZipFileName);
        Assert.Matches("^RA-[0-9]{8}-[0-9]{1,5}$", document.Identifier);
        Assert.Equal("2.0", xml.XPathSelectElement("/va:VoidedDocuments/cbc:UBLVersionID", Namespaces)!.Value);
        Assert.Equal("1.0", xml.XPathSelectElement("/va:VoidedDocuments/cbc:CustomizationID", Namespaces)!.Value);
        Assert.Equal("2026-09-30", xml.XPathSelectElement("/va:VoidedDocuments/cbc:ReferenceDate", Namespaces)!.Value);
        Assert.Equal("2026-10-01", xml.XPathSelectElement("/va:VoidedDocuments/cbc:IssueDate", Namespaces)!.Value);

        var lines = xml.XPathSelectElements("/va:VoidedDocuments/sac:VoidedDocumentsLine", Namespaces).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(["01", "07"], lines.Select(l => l.XPathSelectElement("cbc:DocumentTypeCode", Namespaces)!.Value).ToArray());
        Assert.Equal("FC01", lines[1].XPathSelectElement("sac:DocumentSerialID", Namespaces)!.Value);
        Assert.Equal("4", lines[1].XPathSelectElement("sac:DocumentNumberID", Namespaces)!.Value);
        Assert.Equal("Error en la emisión", lines[0].XPathSelectElement("sac:VoidReasonDescription", Namespaces)!.Value);
    }

    private static readonly Regex Comment = new(@"\s*\(.*$", RegexOptions.Singleline);

    [Fact]
    public void Every_mandatory_tag_of_the_workbook_is_present()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var paths = new List<string>();
        using (var stream = File.OpenRead(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx")))
        using (var reader = ExcelReaderFactory.CreateReader(stream))
        {
            do
            {
                if (reader.Name?.StartsWith("Comunicaci", StringComparison.Ordinal) != true)
                {
                    continue;
                }

                while (reader.Read())
                {
                    var condition = reader.FieldCount > 4 ? reader.GetValue(4)?.ToString()?.Trim() : null;
                    var tag = reader.FieldCount > 7 ? reader.GetValue(7)?.ToString()?.Trim() : null;
                    if (condition == "M" && tag is not null && tag.StartsWith("/VoidedDocuments/", StringComparison.Ordinal))
                    {
                        var path = Comment.Replace(tag, string.Empty);
                        var at = path.IndexOf('@', StringComparison.Ordinal);
                        paths.Add(at >= 0 ? path[..at] : path);
                    }
                }

                break;
            }
            while (reader.NextResult());
        }

        var xml = XDocument.Parse(_generator.Generate(Data()).Value.Xml);
        var mandatory = paths.Distinct().Where(p => !p.Contains("ds:Signature", StringComparison.Ordinal)).ToList();
        Assert.True(mandatory.Count >= 8, $"Expected the workbook to list mandatory tags, found {mandatory.Count}.");

        var missing = mandatory
            .Select(p => p.Replace("/VoidedDocuments", "/va:VoidedDocuments", StringComparison.Ordinal))
            .Where(p => !xml.XPathSelectElements(p, Namespaces).Any())
            .ToList();

        Assert.True(missing.Count == 0, "Missing mandatory tags: " + string.Join("; ", missing));
    }

    public static TheoryData<string, VoidedData> InvalidData() => new()
    {
        { "no lines", Data() with { Lines = [] } },
        { "bad ruc", Data() with { Ruc = "1" } },
        { "correlative 0", Data() with { Correlative = 0 } },
        { "generated before issued", Data() with { IssueDate = new DateOnly(2026, 9, 29) } },
        { "receipt", Data(Line(1, "03", "B001")) },
        { "series of a receipt", Data(Line(1, "01", "B001")) },
        { "number zero", Data(Line(1, number: 0)) },
        { "number 9 digits", Data(Line(1, number: 100_000_000)) },
        { "duplicate", Data(Line(1), Line(2)) },
        { "line numbers", Data(Line(2)) },
        { "short reason", Data(Line(1, reason: "ab")) },
        { "long reason", Data(Line(1, reason: new string('x', 101))) },
        { "reason with newline", Data(Line(1, reason: "a\nb c")) },
    };

    [Theory]
    [MemberData(nameof(InvalidData))]
    public void Invalid_communications_are_refused_not_emitted(string name, VoidedData data)
    {
        var result = _generator.Generate(data);

        Assert.False(result.IsSuccess, name);
        Assert.Equal(ErrorCodes.CpeInvalidDocument, result.Error.Code);
    }

    [Fact]
    public void The_same_series_and_number_may_be_voided_for_different_types_and_the_limit_is_500()
    {
        Assert.True(_generator.Generate(Data(Line(1, "01", "F001", 1), Line(2, "07", "F001", 1))).IsSuccess);
        Assert.True(_generator.Generate(Data(Enumerable.Range(1, 500).Select(i => Line(i, number: i)).ToArray())).IsSuccess);
        Assert.False(_generator.Generate(Data(Enumerable.Range(1, 501).Select(i => Line(i, number: i)).ToArray())).IsSuccess);
    }

    [Fact]
    public void A_communication_can_be_signed_and_stays_valid()
    {
        using var certificate = SigningTests.NewCertificate();
        var signed = new XmlDsigSigner().Sign(_generator.Generate(Data()).Value.Xml, certificate);

        Assert.True(signed.IsSuccess, signed.IsSuccess ? null : signed.Error.Detail);
        Assert.True(new XmlDsigSigner().Verify(signed.Value.Xml).Value.IsValid);
    }
}
