using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;

namespace SecureFact.Unit.Tests.Gre;

/// <summary>The guides of import (08), export (09), foreign goods (19) and itinerant issuer (18) against the sheet «Guía-Remitente2_0» of S27 and the UBL 2.1 schema of S30 (ADR-059).</summary>
public class GreCustomsTests
{
    private const string Declaration = "118-2026-10-123456";
    private const string ExportDeclaration = "118-2026-40-654321";
    private const string ForeignDeclaration = "118-2026-20-777";

    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Root = "urn:oasis:names:specification:ubl:schema:xsd:DespatchAdvice-2";

    private static readonly GrePartyInput Importer = new("6", GreSamples.CustomerRuc, "IMPORTADORA DEMO SAC");
    private static readonly GreCustomsInput CallaoPort = new(PortCode: "CLL", PortType: "1", PortName: "Callao");

    /// <summary>An import (08): the whole declaration travels in a lot of 3 packages that leaves from the port of Callao.</summary>
    private static CreateGreRequest Import() => GreSamples.PrivateSale() with
    {
        MotiveCode = "08",
        Recipient = Importer,
        Origin = new GreAddressInput("070101", "Terminal portuario del Callao"),
        PackageCount = 3,
        Goods = [],
        RelatedDocuments = [new GreRelatedDocumentInput("50", Declaration, null)],
        Customs = CallaoPort with { WholeTransfer = true },
    };

    /// <summary>An export (09): two goods of a declaration of the regime 40, the port of Callao as the point of arrival.</summary>
    private static CreateGreRequest Export() => GreSamples.PrivateSale() with
    {
        MotiveCode = "09",
        Recipient = Importer,
        Destination = new GreAddressInput("070101", "Terminal portuario del Callao"),
        PackageCount = 12,
        Goods =
        [
            new GreGoodInput("Café en grano", "2U", 100m, Customs: new GreGoodCustomsInput(DeclarationNumber: ExportDeclaration, DeclarationSeries: "1")),
            new GreGoodInput("Cacao en grano", "2U", 50m, Customs: new GreGoodCustomsInput(DeclarationNumber: ExportDeclaration, DeclarationSeries: "2")),
        ],
        RelatedDocuments = [new GreRelatedDocumentInput("50", ExportDeclaration, null)],
        Customs = CallaoPort with { NetWeight = 1450.5m, WeightNote = "El peso bruto incluye los sacos y las paletas" },
    };

    /// <summary>Foreign goods (19) that travel with their declaration of the regime 20 from the airport of Arequipa to a warehouse of the recipient.</summary>
    private static CreateGreRequest Foreign() => GreSamples.PrivateSale() with
    {
        MotiveCode = "19",
        Recipient = Importer,
        Origin = new GreAddressInput("040104", "Aeropuerto Rodríguez Ballón"),
        Destination = new GreAddressInput("150101", "Depósito autorizado", GreSamples.CustomerRuc, "0001"),
        PackageCount = 4,
        Goods = [],
        RelatedDocuments = [new GreRelatedDocumentInput("50", ForeignDeclaration.Replace("777", "123456", StringComparison.Ordinal), null)],
        Customs = new GreCustomsInput("AQP", "2", "Rodríguez Ballón", WholeTransfer: true),
    };

    /// <summary>Foreign goods (19) of a cargo manifest: two lines of a transport document, one container each, from the port of Callao.</summary>
    private static CreateGreRequest Manifest(bool containers) => Foreign() with
    {
        Origin = new GreAddressInput("070101", "Terminal portuario del Callao"),
        PackageCount = containers ? null : 4,
        RelatedDocuments = [new GreRelatedDocumentInput("91", "01-118-1-2026-45", null)],
        Goods = containers
            ?
            [
                new GreGoodInput("Contenedor con repuestos", "U", 1m, Customs: new GreGoodCustomsInput(TransportDocument: "HBL-998", TransportDetail: "1", ManifestContainer: "MSKU1234567", Seal: "SEAL001", EmptyContainer: false)),
                new GreGoodInput("Contenedor vacío", "U", 1m, Customs: new GreGoodCustomsInput(TransportDocument: "HBL-998", TransportDetail: "2", ManifestContainer: "MSKU7654321", EmptyContainer: true)),
            ]
            : [new GreGoodInput("Repuestos", "U", 40m, Customs: new GreGoodCustomsInput(TransportDocument: "HBL-998", TransportDetail: "1"))],
        Customs = CallaoPort with { ManifestContainers = containers, Containers = containers ? [new GreContainerInput("MSKU1234567", "SEAL001"), new GreContainerInput("MSKU7654321")] : null },
    };

    /// <summary>Foreign goods (19) with the delivery order of the port terminal: nothing else is said.</summary>
    private static CreateGreRequest Order() => Foreign() with
    {
        Origin = new GreAddressInput("070101", "Terminal portuario del Callao"),
        GrossWeight = 0,
        PackageCount = null,
        RelatedDocuments = [new GreRelatedDocumentInput("92", "12345678901234", "20100070970")],
        Customs = CallaoPort,
    };

    /// <summary>The itinerant issuer (18) sends the goods to itself and does not know where they go.</summary>
    private static CreateGreRequest Itinerant() => GreSamples.PrivateSale() with
    {
        MotiveCode = "18",
        Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "REMITENTE DEMO SAC"),
        Destination = null,
        RelatedDocuments = null,
    };

    private static IReadOnlyList<string> Check(CreateGreRequest request, GreValidationContext? context = null) => GreValidator.Validate(request, context ?? GreSamples.Context());

    private static void AssertRule(string rule, IReadOnlyList<string> issues) =>
        Assert.Contains(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    private static void AssertNoRule(string rule, IReadOnlyList<string> issues) =>
        Assert.DoesNotContain(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    private static void AssertValid(CreateGreRequest request)
    {
        var issues = Check(request);
        Assert.True(issues.Count == 0, string.Join(" | ", issues));
    }

    // ---------- the five complete guides ----------

    [Fact]
    public void An_import_an_export_foreign_goods_and_an_itinerant_issuer_are_valid()
    {
        AssertValid(Import());
        AssertValid(Export());
        AssertValid(Foreign());
        AssertValid(Manifest(containers: false));
        AssertValid(Manifest(containers: true));
        AssertValid(Order());
        AssertValid(Itinerant());
    }

    // ---------- related documents ----------

    [Fact]
    public void Import_and_export_need_a_declaration_and_accept_no_invoice()
    {
        var import = Import();

        AssertRule("3440", Check(import with { RelatedDocuments = null }));
        AssertRule("3440", Check(import with { RelatedDocuments = [new GreRelatedDocumentInput("09", "T001-5", GreSamples.SenderRuc)] }));
        AssertRule("3445", Check(import with { RelatedDocuments = [.. import.RelatedDocuments!, new GreRelatedDocumentInput("01", "F001-5", GreSamples.SenderRuc)] }));
        AssertRule("3445", Check(GreSamples.PrivateSale() with { RelatedDocuments = [new GreRelatedDocumentInput("50", Declaration, null)] }));
    }

    [Theory]
    [InlineData("08", "50", "118-2026-10-123456", true)]
    [InlineData("08", "50", "118-2026-40-123456", false)]
    [InlineData("08", "52", "118-2026-18-1", true)]
    [InlineData("08", "52", "118-2026-10-1", false)]
    [InlineData("09", "50", "118-2026-40-1", true)]
    [InlineData("09", "50", "118-2026-10-1", false)]
    [InlineData("09", "52", "118-2026-48-9", true)]
    [InlineData("19", "50", "118-2026-36-9", true)]
    [InlineData("19", "50", "118-2026-40-9", false)]
    [InlineData("08", "50", "118-2026-10-012345", false)]
    [InlineData("08", "50", "11-2026-10-1", false)]
    public void A_declaration_has_the_regime_of_its_motive(string motive, string type, string number, bool valid)
    {
        var request = motive switch { "09" => Export(), "19" => Foreign(), _ => Import() } with { RelatedDocuments = [new GreRelatedDocumentInput(type, number, null)] };

        var issues = Check(request);

        if (valid)
        {
            AssertNoRule("3441", issues);
        }
        else
        {
            AssertRule("3441", issues);
        }
    }

    [Fact]
    public void The_cargo_manifest_and_the_delivery_order_follow_the_rules_of_foreign_goods()
    {
        var foreign = Foreign();

        AssertRule("3493", Check(foreign with { RelatedDocuments = null }));
        AssertRule("3463", Check(foreign with { RelatedDocuments = [.. foreign.RelatedDocuments!, new GreRelatedDocumentInput("91", "01-118-1-2026-45", null)] }));
        AssertRule("3613", Check(Order() with { RelatedDocuments = [new GreRelatedDocumentInput("92", "123", "20100070970"), new GreRelatedDocumentInput("91", "01-118-1-2026-45", null)] }));
        AssertRule("3445", Check(foreign with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-5", GreSamples.SenderRuc)] }));
        AssertRule("3445", Check(Import() with { RelatedDocuments = [.. Import().RelatedDocuments!, new GreRelatedDocumentInput("92", "123", "20100070970")] }));
        AssertRule("3441", Check(Manifest(false) with { RelatedDocuments = [new GreRelatedDocumentInput("91", "01-118-4-2026-45", null)] })); // via 4 is the airport
        AssertRule("3441", Check(Order() with { RelatedDocuments = [new GreRelatedDocumentInput("92", "ABC", "20100070970")] }));
        AssertRule("3380", Check(Order() with { RelatedDocuments = [new GreRelatedDocumentInput("92", "123", null)] }));
    }

    // ---------- parties ----------

    [Fact]
    public void The_recipient_of_an_export_is_not_the_sender_and_the_one_of_foreign_goods_has_a_RUC()
    {
        AssertRule("2555", Check(Export() with { Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "YO MISMO") }));
        AssertRule("3417", Check(Foreign() with { Recipient = new GrePartyInput("1", "12345678", "PERSONA") }));
    }

    [Fact]
    public void The_itinerant_issuer_sends_to_itself_and_has_no_point_of_arrival()
    {
        var itinerant = Itinerant();

        AssertRule("2554", Check(itinerant with { Recipient = Importer }));
        AssertRule("3416", Check(itinerant with { Destination = new GreAddressInput("150122", "Calle Los Pinos 456") }));
        AssertRule("2775", Check(GreSamples.PrivateSale() with { Destination = null }));
    }

    // ---------- points and port ----------

    [Fact]
    public void The_point_of_departure_of_an_import_is_the_port_and_without_a_port_it_is_an_establishment()
    {
        var import = Import();

        AssertRule("3364", Check(import with { Origin = new GreAddressInput("150122", "Calle Los Pinos 456") }));
        var noPort = import with { Customs = new GreCustomsInput(WholeTransfer: true), Origin = new GreAddressInput("150122", "Depósito temporal") };
        AssertRule("3365", Check(noPort));
        AssertNoRule("3365", Check(noPort with { Origin = new GreAddressInput("150122", "Depósito temporal", GreSamples.CustomerRuc, "0001") }));
        AssertRule("3411", Check(noPort with { Origin = new GreAddressInput("150122", "Depósito temporal", GreSamples.SenderRuc, "0001") }));
    }

    [Fact]
    public void The_point_of_arrival_of_an_export_is_the_port_or_an_establishment()
    {
        var export = Export();

        AssertRule("3364", Check(export with { Destination = new GreAddressInput("150122", "Calle Los Pinos 456") }));
        AssertRule("3369", Check(export with { Customs = export.Customs! with { PortCode = null, PortType = null, PortName = null }, Destination = new GreAddressInput("150122", "Calle Los Pinos 456") }));
    }

    [Fact]
    public void Foreign_goods_name_the_port_or_airport_and_arrive_at_an_establishment_of_the_recipient()
    {
        var foreign = Foreign();

        AssertRule("3483", Check(foreign with { Customs = new GreCustomsInput(WholeTransfer: true) }));
        AssertRule("3369", Check(foreign with { Destination = new GreAddressInput("150101", "Depósito autorizado") }));
        AssertRule("3488", Check(foreign with { Destination = new GreAddressInput("150101", "Depósito autorizado", GreSamples.SenderRuc, "0001") }));
        AssertRule("3364", Check(foreign with { Origin = new GreAddressInput("150101", "Av. Argentina 123") }));
    }

    [Fact]
    public void The_port_has_a_type_a_code_of_its_catalogue_and_a_name()
    {
        var import = Import();

        AssertRule("4416", Check(import with { Customs = import.Customs! with { PortType = null } }));
        AssertRule("3459", Check(import with { Customs = import.Customs! with { PortCode = "ZZZ" } }));
        AssertRule("3460", Check(import with { Customs = import.Customs! with { PortCode = "ZZZ", PortType = "2" } }));
        AssertRule("4418", Check(import with { Customs = import.Customs! with { PortName = " " } }));
        AssertRule("4413", Check(import with { Customs = import.Customs! with { PortCode = null } }));
    }

    // ---------- weights, packages and containers ----------

    [Fact]
    public void An_export_states_the_net_weight_and_why_the_gross_weight_differs()
    {
        var export = Export();

        AssertRule("4383", Check(export with { Customs = export.Customs! with { NetWeight = null } }));
        AssertRule("4387", Check(export with { Customs = export.Customs! with { WeightNote = null } }));
        AssertRule("3397", Check(export with { Customs = export.Customs! with { NetWeight = 0 } }));
        AssertRule("4428", Check(export with { Customs = export.Customs! with { WeightNote = "dos\nlíneas" } }));
        AssertNoRule("4383", Check(export with { Customs = export.Customs! with { NetWeight = null, WholeTransfer = true }, Goods = [] }));
    }

    [Fact]
    public void The_goods_travel_in_packages_or_in_containers_but_not_both()
    {
        var import = Import();
        var container = new GreContainerInput("MSKU1234567", "SEAL001");

        AssertRule("3419", Check(import with { PackageCount = null }));
        AssertRule("3621", Check(import with { Customs = import.Customs! with { Containers = [container] } }));
        AssertNoRule("3419", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [container] } }));
        AssertRule("3420", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [container, container with { Number = "B1" }, container with { Number = "B2" }] } }));
        AssertRule("3421", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [container, container with { Seal = "OTRO" }] } }));
        AssertRule("3423", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [container, container with { Number = "B1" }] } }));
        AssertRule("4071", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [container with { Number = "contenedor 1" }] } }));
        AssertRule("4074", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [container with { Seal = "000" }] } }));
    }

    [Fact]
    public void A_container_of_a_guide_with_the_whole_declaration_has_a_seal()
    {
        var import = Import();

        AssertRule("3422", Check(import with { PackageCount = null, Customs = import.Customs! with { Containers = [new GreContainerInput("MSKU1234567")] } }));
    }

    [Fact]
    public void The_delivery_order_carries_no_weight_no_packages_no_containers_and_no_goods()
    {
        var order = Order();

        AssertRule("3625", Check(order with { GrossWeight = 10 }));
        AssertRule("3626", Check(order with { PackageCount = 2 }));
        AssertRule("3627", Check(order with { Customs = order.Customs! with { Containers = [new GreContainerInput("MSKU1234567", "S1")] } }));
        AssertRule("3623", Check(order with { Customs = order.Customs! with { NetWeight = 5 } }));
        AssertRule("3624", Check(order with { Customs = order.Customs! with { WeightNote = "nota" } }));
        AssertRule("3629", Check(order with { Goods = [new GreGoodInput("Algo", "U", 1m)] }));
    }

    // ---------- goods ----------

    [Fact]
    public void The_goods_of_an_export_say_their_declaration_and_their_series_and_use_the_units_of_customs()
    {
        var export = Export();
        var good = export.Goods[0];

        AssertRule("2580", Check(export with { Goods = [] }));
        AssertRule("3427", Check(export with { Goods = [good with { Customs = new GreGoodCustomsInput(DeclarationSeries: "1") }] }));
        AssertRule("3428", Check(export with { Goods = [good with { Customs = new GreGoodCustomsInput(DeclarationNumber: ExportDeclaration) }] }));
        AssertRule("3430", Check(export with { Goods = [good with { Customs = new GreGoodCustomsInput(DeclarationNumber: "118-2026-40-111111", DeclarationSeries: "1") }] }));
        AssertRule("2769", Check(export with { Goods = [good with { Customs = new GreGoodCustomsInput(DeclarationNumber: "ABC", DeclarationSeries: "1") }] }));
        AssertRule("3431", Check(export with { Goods = [good with { Customs = good.Customs! with { DeclarationSeries = "12345" } }] }));
        AssertRule("3446", Check(export with { Goods = [good with { UnitCode = "XXX" }] }));
        AssertRule("2780", Check(export with { Goods = [good with { Quantity = 0 }] }));
    }

    [Fact]
    public void The_lines_of_a_manifest_have_their_document_their_detail_and_with_containers_their_container()
    {
        var manifest = Manifest(containers: false);
        var good = manifest.Goods[0];

        AssertRule("3490", Check(manifest with { Goods = [good with { Customs = null }] }));
        AssertRule("3491", Check(manifest with { Goods = [good with { Customs = new GreGoodCustomsInput(TransportDocument: "HBL-998") }] }));
        AssertRule("3466", Check(manifest with { Goods = [good with { Customs = good.Customs! with { TransportDocument = "hbl 998" } }] }));
        AssertRule("3468", Check(manifest with { Goods = [good with { Customs = good.Customs! with { TransportDetail = "0" } }] }));
        AssertRule("3446", Check(manifest with { Goods = [good with { UnitCode = "NIU" }] }));
        AssertRule("2780", Check(manifest with { Goods = [good with { Quantity = 1.5m }] }));
        AssertRule("3472", Check(manifest with { Goods = [good, good] }));
        AssertRule("2580", Check(manifest with { Goods = [] }));

        var withContainers = Manifest(containers: true);
        var line = withContainers.Goods[0];
        AssertRule("3480", Check(withContainers with { Goods = [line with { Customs = line.Customs! with { ManifestContainer = null } }] }));
        AssertRule("3486", Check(withContainers with { Goods = [line with { Customs = line.Customs! with { ManifestContainer = "OTRO1234567" } }] }));
        AssertRule("3482", Check(withContainers with { Goods = [line with { Customs = line.Customs! with { EmptyContainer = null } }] }));
        AssertRule("3481", Check(withContainers with { Goods = [line with { Customs = line.Customs! with { Seal = null } }] }));
        AssertRule("3492", Check(withContainers with { Goods = [line, line] }));
        AssertRule("3487", Check(withContainers with { Customs = withContainers.Customs! with { Containers = null } }));
    }

    // ---------- indicators outside their motives ----------

    [Fact]
    public void The_customs_data_does_not_exist_with_the_other_motives()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("3392", Check(sale with { Customs = new GreCustomsInput(WholeTransfer: true) }));
        AssertRule("3478", Check(sale with { Customs = new GreCustomsInput(ManifestContainers: true) }));
        AssertRule("3395", Check(sale with { Customs = new GreCustomsInput(NetWeight: 5) }));
        AssertRule("3418", Check(sale with { Customs = new GreCustomsInput(WeightNote: "nota") }));
        AssertRule("SF", Check(sale with { Customs = new GreCustomsInput(PortCode: "CLL", PortType: "1", PortName: "Callao") }));
        AssertRule("3485", Check(Manifest(false) with { Customs = Manifest(false).Customs! with { WholeTransfer = true } }));
        AssertRule("3478", Check(Import() with { Customs = Import().Customs! with { ManifestContainers = true } }));
    }

    // ---------- XML ----------

    private static GreXmlData Data(CreateGreRequest request) =>
        new(GreSamples.SenderRuc, "EMISORA DEMO SAC", "T001", 1, GreSamples.Today, new TimeOnly(10, 30, 5), request, GreSamples.DocumentNames);

    private static XDocument Build(CreateGreRequest request) => XDocument.Parse(GreUblGenerator.Generate(Data(request)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureFact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void AssertSchema(XDocument document)
    {
        var signed = new XDocument(document);
        signed.Descendants(Ext + "ExtensionContent").First().Add(
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
        var assets = Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.1");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(Root.NamespaceName, Path.Combine(assets, "maindoc", "UBL-DespatchAdvice-2.1.xsd"));
        using var dsig = XmlReader.Create(Path.Combine(assets, "common", "UBL-xmldsig-core-schema-2.1.xsd"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);
        var errors = new List<string>();
        signed.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    [Fact]
    public void Every_guide_with_customs_data_passes_the_schema()
    {
        foreach (var request in new[] { Import(), Export(), Foreign(), Manifest(false), Manifest(true), Order(), Itinerant() })
        {
            AssertSchema(Build(request));
        }
    }

    [Fact]
    public void An_import_carries_the_port_the_indicator_of_the_whole_declaration_and_a_line_of_form()
    {
        var xml = Build(Import());

        var port = xml.Descendants(Cac + "FirstArrivalPortLocation").Single();
        Assert.Equal("CLL", port.Element(Cbc + "ID")!.Value);
        Assert.Equal("1", port.Element(Cbc + "LocationTypeCode")!.Value);
        Assert.Equal("Callao", port.Element(Cbc + "Name")!.Value);
        Assert.Contains(xml.Descendants(Cbc + "SpecialInstructions"), e => e.Value == "SUNAT_Envio_IndicadorTrasladoTotalDAMoDS");
        Assert.Equal("08", xml.Descendants(Cbc + "HandlingCode").Single().Value);
        Assert.Equal("3", xml.Descendants(Cbc + "TotalTransportHandlingUnitQuantity").Single().Value);
        var line = Assert.Single(xml.Descendants(Cac + "DespatchLine"));
        Assert.Equal("1", line.Element(Cbc + "ID")!.Value);
        Assert.Null(line.Element(Cbc + "DeliveredQuantity"));
        Assert.Equal(Declaration, xml.Descendants(Cac + "AdditionalDocumentReference").Single().Element(Cbc + "ID")!.Value);
    }

    [Fact]
    public void An_export_carries_the_net_weight_the_note_and_the_properties_of_each_good()
    {
        var xml = Build(Export());

        Assert.Equal("1450.5", xml.Descendants(Cbc + "NetWeightMeasure").Single().Value);
        Assert.Equal("KGM", xml.Descendants(Cbc + "NetWeightMeasure").Single().Attribute("unitCode")!.Value);
        Assert.Equal("El peso bruto incluye los sacos y las paletas", xml.Descendants(Cac + "Shipment").Single().Element(Cbc + "Information")!.Value);
        var lines = xml.Descendants(Cac + "DespatchLine").ToList();
        Assert.Equal(2, lines.Count);
        var properties = lines[0].Descendants(Cac + "AdditionalItemProperty").ToDictionary(p => p.Element(Cbc + "NameCode")!.Value, p => p.Element(Cbc + "Value")!.Value);
        Assert.Equal(ExportDeclaration, properties["7021"]);
        Assert.Equal("1", properties["7023"]);
        Assert.Equal("55", lines[0].Descendants(Cbc + "NameCode").First().Attribute("listURI")!.Value[^2..]);
    }

    [Fact]
    public void A_manifest_with_containers_has_the_packages_the_properties_and_no_quantity()
    {
        var xml = Build(Manifest(containers: true));

        var packages = xml.Descendants(Cac + "Package").ToList();
        Assert.Equal(2, packages.Count);
        Assert.Equal("MSKU1234567", packages[0].Element(Cbc + "ID")!.Value);
        Assert.Equal("SEAL001", packages[0].Element(Cbc + "TraceID")!.Value);
        Assert.Null(packages[1].Element(Cbc + "TraceID"));
        Assert.Contains(xml.Descendants(Cbc + "SpecialInstructions"), e => e.Value == "SUNAT_Envio_IndicadorTrasladoContenedorManifiestoCarga");
        Assert.Empty(xml.Descendants(Cbc + "DeliveredQuantity"));
        Assert.Empty(xml.Descendants(Cbc + "TotalTransportHandlingUnitQuantity"));
        var first = xml.Descendants(Cac + "DespatchLine").First().Descendants(Cac + "AdditionalItemProperty").ToDictionary(p => p.Element(Cbc + "NameCode")!.Value, p => p.Element(Cbc + "Value")!.Value);
        Assert.Equal(["7024", "7025", "7026", "7027", "7028"], first.Keys.Order().ToArray());
        Assert.Equal("0", first["7028"]);
    }

    [Fact]
    public void The_delivery_order_and_the_itinerant_issuer_leave_out_what_they_do_not_have()
    {
        var order = Build(Order());
        Assert.Empty(order.Descendants(Cbc + "GrossWeightMeasure"));
        Assert.Single(order.Descendants(Cac + "DespatchLine"));
        Assert.Equal("92", order.Descendants(Cac + "AdditionalDocumentReference").Single().Element(Cbc + "DocumentTypeCode")!.Value);

        var itinerant = Build(Itinerant());
        Assert.Empty(itinerant.Descendants(Cac + "DeliveryAddress"));
        Assert.NotEmpty(itinerant.Descendants(Cac + "DespatchAddress"));
        Assert.Equal("18", itinerant.Descendants(Cbc + "HandlingCode").Single().Value);
    }
}
