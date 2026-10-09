using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;

namespace SecureFact.Unit.Tests.Gre;

/// <summary>The guide of the carrier (type 31) against the sheet «Guía-Transportista2_0» of S27 and the UBL 2.1 schema of S30.</summary>
public class GreCarrierTests
{
    private const string SenderGuideIssuer = "20100066603";

    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Root = "urn:oasis:names:specification:ubl:schema:xsd:DespatchAdvice-2";

    /// <summary>The carrier is the company whose RUC the context holds; the sender is another taxpayer.</summary>
    private static GreValidationContext Context() => GreSamples.Context() with { SenderRuc = GreSamples.CarrierRuc };

    internal static CreateGreCarrierRequest WithGoods() => new(
        CompanyId: Guid.Empty,
        SeriesId: Guid.Empty,
        IssueDate: GreSamples.Today,
        TransferStartDate: GreSamples.Today,
        GrossWeight: 1500.5m,
        WeightUnit: "KGM",
        PackageCount: 10,
        Note: "Carga frágil",
        MtcRegistration: "MTC123456",
        Sender: new GrePartyInput("6", SenderGuideIssuer, "REMITENTE DEMO SAC"),
        Recipient: new GrePartyInput("6", GreSamples.CustomerRuc, "CLIENTE DEMO SAC"),
        Origin: new GreAddressInput("150101", "Av. Argentina 123, Lima"),
        Destination: new GreAddressInput("040101", "Calle Mercaderes 45, Arequipa"),
        Vehicle: new GreVehicleInput("ABC123", "1234567890"),
        SecondaryVehicles: [new GreVehicleInput("XYZ987", "0987654321")],
        Driver: new GreDriverInput("1", "12345678", "JUAN CARLOS", "PEREZ GOMEZ", "Q12345678"),
        SecondaryDrivers: null,
        Goods: [new GreGoodInput("Cajas de repuestos", "NIU", 10m, "REP-01")],
        RelatedDocuments: [new GreRelatedDocumentInput("01", "F001-123", SenderGuideIssuer)]);

    /// <summary>The same transfer when the guide of the sender already lists the goods and the addresses.</summary>
    internal static CreateGreCarrierRequest WithSenderGuide() => WithGoods() with
    {
        Goods = null,
        Origin = new GreAddressInput("150101", string.Empty),
        Destination = new GreAddressInput("040101", string.Empty),
        RelatedDocuments = [new GreRelatedDocumentInput("09", "T001-45", SenderGuideIssuer)],
    };

    private static IReadOnlyList<string> Check(CreateGreCarrierRequest request, GreValidationContext? context = null) => GreValidator.ValidateCarrier(request, context ?? Context());

    private static void AssertRule(string rule, IReadOnlyList<string> issues) =>
        Assert.Contains(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    private static void AssertNoRule(string rule, IReadOnlyList<string> issues) =>
        Assert.DoesNotContain(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    // ---------- validator ----------

    [Fact]
    public void A_transfer_with_its_goods_and_one_that_relies_on_the_guide_of_the_sender_are_valid()
    {
        Assert.Empty(Check(WithGoods()));
        Assert.Empty(Check(WithSenderGuide()));
    }

    [Fact]
    public void The_sender_is_not_the_carrier_and_both_parties_are_complete()
    {
        var request = WithGoods();

        AssertRule("2560", Check(request with { Sender = new GrePartyInput("6", GreSamples.CarrierRuc, "EL PROPIO TRANSPORTISTA") }));
        AssertRule("3383", Check(request with { Sender = null! }));
        AssertRule("3383", Check(request with { Sender = new GrePartyInput("6", string.Empty, "SIN NUMERO") }));
        AssertRule("2542", Check(request with { Sender = new GrePartyInput("Z", "ABC123", "TIPO RARO") }));
        AssertRule("2757", Check(request with { Recipient = null! }));
        AssertRule("2760", Check(request with { Recipient = new GrePartyInput("Z", "ABC123", "TIPO RARO") }));
    }

    [Fact]
    public void The_dates_the_note_and_the_registration_follow_the_header_rules()
    {
        var request = WithGoods();

        AssertRule("2329", Check(request with { IssueDate = GreSamples.Today.AddDays(1) }));
        AssertRule("3343", Check(request with { TransferStartDate = GreSamples.Today.AddDays(-1) }));
        AssertRule("4186", Check(request with { Note = "dos\nlíneas" }));
        AssertRule("4392", Check(request with { MtcRegistration = "mtc 1" }));
        AssertRule("2523", Check(request with { GrossWeight = 0 }));
        AssertRule("2523", Check(request with { WeightUnit = "LBR" }));
    }

    [Fact]
    public void The_principal_vehicle_and_driver_are_required_with_the_circulation_card()
    {
        var request = WithGoods();

        AssertRule("2566", Check(request with { Vehicle = null! }));
        AssertRule("3357", Check(request with { Driver = null! }));
        AssertRule("4399", Check(request with { Vehicle = new GreVehicleInput("ABC123", null) }));
        AssertRule("4399", Check(request with { SecondaryVehicles = [new GreVehicleInput("XYZ987", null)] }));
        AssertRule("2567", Check(request with { Vehicle = new GreVehicleInput("A-1", "1234567890") }));
        AssertRule("2571", Check(request with { Driver = request.Driver with { DocumentTypeCode = "6" } }));
        AssertRule("2573", Check(request with { Driver = request.Driver with { LicenseNumber = "123" } }));
    }

    [Fact]
    public void At_most_two_secondary_vehicles_and_drivers_with_different_licences()
    {
        var request = WithGoods();
        var vehicle = new GreVehicleInput("XYZ987", "0987654321");
        var driver = new GreDriverInput("1", "87654321", "ANA", "RAMOS", "B87654321");

        AssertRule("4389", Check(request with { SecondaryVehicles = [vehicle, vehicle, vehicle] }));
        AssertRule("4376", Check(request with { SecondaryDrivers = [driver, driver, driver] }));
        AssertRule("3362", Check(request with { SecondaryDrivers = [driver, driver] }));
        Assert.Empty(Check(request with { SecondaryDrivers = [driver] }));
    }

    [Fact]
    public void Without_a_guide_of_the_sender_the_goods_and_the_addresses_are_required()
    {
        var request = WithGoods();

        AssertRule("3435", Check(request with { Goods = null }));
        AssertRule("2577", Check(request with { Origin = new GreAddressInput("150101", string.Empty) }));
        AssertRule("2574", Check(request with { Destination = new GreAddressInput("040101", string.Empty) }));
        AssertRule("2776", Check(request with { Origin = new GreAddressInput("15", "Av. Argentina 123, Lima") }));
        AssertRule("2780", Check(request with { Goods = [new GreGoodInput("Cajas", "NIU", 0m)] }));
    }

    [Fact]
    public void A_planned_transshipment_makes_the_addresses_optional()
    {
        var request = WithGoods() with { PlannedTransshipment = true, Origin = new GreAddressInput("150101", string.Empty), Destination = new GreAddressInput("040101", string.Empty) };

        AssertNoRule("2577", Check(request));
        AssertNoRule("2574", Check(request));
    }

    [Fact]
    public void With_a_guide_of_the_sender_the_goods_are_not_listed_again_and_more_documents_are_allowed()
    {
        var request = WithSenderGuide();

        AssertRule("4434", Check(request with { Goods = [new GreGoodInput("Cajas", "NIU", 1m)] }));
        Assert.Empty(Check(request with { RelatedDocuments = [.. request.RelatedDocuments!, new GreRelatedDocumentInput("01", "F001-9", SenderGuideIssuer)] }));
        AssertRule("3346", Check(WithGoods() with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-9", SenderGuideIssuer), new GreRelatedDocumentInput("03", "B001-9", SenderGuideIssuer)] }));
    }

    [Fact]
    public void The_related_documents_have_their_shape_their_issuer_and_the_sender_of_the_guide()
    {
        var request = WithGoods();

        AssertRule("3441", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("01", "XX", SenderGuideIssuer)] }));
        AssertRule("3380", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-1", null)] }));
        AssertRule("3409", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-1", "123")] }));
        AssertRule("3403", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("01", " ", SenderGuideIssuer)] }));
        AssertRule("3340", Check(WithSenderGuide() with { RelatedDocuments = [new GreRelatedDocumentInput("09", "T001-45", SenderGuideIssuer), new GreRelatedDocumentInput("09", "T001-45", SenderGuideIssuer)] }));
        AssertRule("3381", Check(WithSenderGuide() with { RelatedDocuments = [new GreRelatedDocumentInput("09", "T001-45", GreSamples.CustomerRuc)] }));
        AssertRule("3445", Check(WithSenderGuide() with { RelatedDocuments = [new GreRelatedDocumentInput("09", "0001-45", SenderGuideIssuer)] }));
        AssertRule("3445", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("50", "118-2026-10-1", null)] }));
        AssertRule("2692", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("49", "123", null)] }));
        AssertRule("3445", Check(request with { RelatedDocuments = [new GreRelatedDocumentInput("31", "V001-1", SenderGuideIssuer)] }));
    }

    [Fact]
    public void The_subcontractor_and_the_payer_of_the_freight_are_stated_when_they_exist()
    {
        var request = WithGoods();

        AssertRule("4424", Check(request with { Subcontracted = true }));
        AssertRule("3391", Check(request with { Subcontracted = true, Subcontractor = new GrePartyInput("1", "12345678", "PERSONA") }));
        AssertRule("4424", Check(request with { Subcontracted = true, Subcontractor = new GrePartyInput("6", "20100066604", "RUC MALO") }));
        AssertRule("3390", Check(request with { Subcontracted = true, Subcontractor = new GrePartyInput("6", GreSamples.CarrierRuc, "YO MISMO") }));
        AssertRule("4426", Check(request with { Subcontracted = true, Subcontractor = new GrePartyInput("6", GreSamples.CustomerRuc, " ") }));
        AssertRule("4424", Check(request with { Subcontractor = new GrePartyInput("6", GreSamples.CustomerRuc, "SUBCONTRATADOR SAC") }));
        Assert.Empty(Check(request with { Subcontracted = true, Subcontractor = new GrePartyInput("6", GreSamples.CustomerRuc, "SUBCONTRATADOR SAC"), FreightPayer = GreFreightPayer.Subcontractor }));

        AssertRule("4402", Check(request with { FreightPayer = GreFreightPayer.ThirdParty }));
        AssertRule("3399", Check(request with { FreightPayer = GreFreightPayer.ThirdParty, ThirdPartyPayer = new GrePartyInput("Z", "AB1", "RARO") }));
        AssertRule("4402", Check(request with { ThirdPartyPayer = new GrePartyInput("6", GreSamples.CustomerRuc, "PAGADOR SAC") }));
        Assert.Empty(Check(request with { FreightPayer = GreFreightPayer.ThirdParty, ThirdPartyPayer = new GrePartyInput("6", GreSamples.CustomerRuc, "PAGADOR SAC") }));
    }

    // ---------- generator ----------

    private static GreCarrierXmlData Data(CreateGreCarrierRequest request) =>
        new(GreSamples.CarrierRuc, "TRANSPORTES DEMO SAC", "V001", 7, GreSamples.Today, new TimeOnly(10, 30, 5), request, GreSamples.DocumentNames);

    private static XDocument Build(CreateGreCarrierRequest request) => XDocument.Parse(GreUblGenerator.GenerateCarrier(Data(request)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureFact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static List<string> SchemaErrors(XDocument document)
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
        using var dsig = XmlReader.Create(
            Path.Combine(assets, "common", "UBL-xmldsig-core-schema-2.1.xsd"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);
        var errors = new List<string>();
        signed.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    private static void AssertValid(XDocument document)
    {
        var errors = SchemaErrors(document);
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    [Fact]
    public void Both_forms_of_the_guide_pass_the_schema()
    {
        AssertValid(Build(WithGoods()));
        AssertValid(Build(WithSenderGuide()));

        var everything = WithGoods() with
        {
            Subcontracted = true,
            Subcontractor = new GrePartyInput("6", GreSamples.CustomerRuc, "SUBCONTRATADOR SAC"),
            FreightPayer = GreFreightPayer.ThirdParty,
            ThirdPartyPayer = new GrePartyInput("6", GreSamples.SenderRuc, "PAGADOR SAC"),
            SecondaryDrivers = [new GreDriverInput("1", "87654321", "ANA", "RAMOS", "B87654321")],
            PlannedTransshipment = true,
            ReturnWithEmptyPackaging = true,
            ReturnEmptyVehicle = true,
        };
        AssertValid(Build(everything));
    }

    [Fact]
    public void The_header_the_parties_and_the_transfer_follow_the_tags_of_the_sheet()
    {
        var xml = Build(WithGoods());
        string Value(params XName[] path)
        {
            XElement? element = xml.Root;
            foreach (var name in path)
            {
                element = element?.Element(name);
            }

            return element?.Value ?? string.Empty;
        }

        Assert.Equal("V001-7", Value(Cbc + "ID"));
        Assert.Equal("31", Value(Cbc + "DespatchAdviceTypeCode"));
        Assert.Equal("2.0", Value(Cbc + "CustomizationID"));
        Assert.Equal(GreSamples.CarrierRuc, Value(Cac + "DespatchSupplierParty", Cac + "Party", Cac + "PartyIdentification", Cbc + "ID"));
        Assert.Equal(GreSamples.CustomerRuc, Value(Cac + "DeliveryCustomerParty", Cac + "Party", Cac + "PartyIdentification", Cbc + "ID"));
        Assert.Equal(SenderGuideIssuer, Value(Cac + "Shipment", Cac + "Delivery", Cac + "Despatch", Cac + "DespatchParty", Cac + "PartyIdentification", Cbc + "ID"));
        Assert.Equal("2026-10-08", Value(Cac + "Shipment", Cac + "ShipmentStage", Cac + "TransitPeriod", Cbc + "StartDate"));
        Assert.Equal("MTC123456", Value(Cac + "Shipment", Cac + "ShipmentStage", Cac + "CarrierParty", Cac + "PartyLegalEntity", Cbc + "CompanyID"));
        Assert.Equal("1500.5", Value(Cac + "Shipment", Cbc + "GrossWeightMeasure"));
        Assert.Equal("ABC123", Value(Cac + "Shipment", Cac + "TransportHandlingUnit", Cac + "TransportEquipment", Cbc + "ID"));
        Assert.Equal("Principal", Value(Cac + "Shipment", Cac + "ShipmentStage", Cac + "DriverPerson", Cbc + "JobTitle"));
        Assert.Contains(xml.Descendants(Cbc + "SpecialInstructions"), e => e.Value == "SUNAT_Envio_IndicadorPagadorFlete_Remitente");
        Assert.Single(xml.Descendants(Cac + "DespatchLine"));
        Assert.Empty(xml.Descendants(Cac + "OriginatorCustomerParty"));
        Assert.Empty(xml.Descendants(Cac + "Consignment"));
    }

    [Fact]
    public void The_guide_of_the_sender_replaces_the_goods_with_an_annotation_and_the_addresses_are_left_out()
    {
        var xml = Build(WithSenderGuide());

        var line = Assert.Single(xml.Descendants(Cac + "DespatchLine"));
        Assert.Equal("0", line.Element(Cbc + "ID")!.Value);
        Assert.Null(line.Element(Cbc + "DeliveredQuantity"));
        Assert.Contains("T001-45", line.Descendants(Cbc + "Description").Single().Value, StringComparison.Ordinal);
        Assert.Empty(xml.Descendants(Cac + "DeliveryAddress").Descendants(Cac + "AddressLine"));
        var related = Assert.Single(xml.Descendants(Cac + "AdditionalDocumentReference"));
        Assert.Equal("T001-45", related.Element(Cbc + "ID")!.Value);
        Assert.Equal("09", related.Element(Cbc + "DocumentTypeCode")!.Value);
    }

    [Fact]
    public void The_subcontractor_and_the_third_party_payer_are_written_where_the_sheet_puts_them()
    {
        var xml = Build(WithGoods() with
        {
            Subcontracted = true,
            Subcontractor = new GrePartyInput("6", GreSamples.CustomerRuc, "SUBCONTRATADOR SAC"),
            FreightPayer = GreFreightPayer.ThirdParty,
            ThirdPartyPayer = new GrePartyInput("6", GreSamples.SenderRuc, "PAGADOR SAC"),
        });

        Assert.Equal(GreSamples.CustomerRuc, xml.Descendants(Cac + "LogisticsOperatorParty").Descendants(Cbc + "ID").Single().Value);
        Assert.Equal(GreSamples.SenderRuc, xml.Descendants(Cac + "OriginatorCustomerParty").Descendants(Cbc + "ID").Single().Value);
        var instructions = xml.Descendants(Cbc + "SpecialInstructions").Select(e => e.Value).ToList();
        Assert.Contains("SUNAT_Envio_IndicadorTrasporteSubcontratado", instructions);
        Assert.Contains("SUNAT_Envio_IndicadorPagadorFlete_Tercero", instructions);
        Assert.DoesNotContain("SUNAT_Envio_IndicadorPagadorFlete_Remitente", instructions);
    }
}
