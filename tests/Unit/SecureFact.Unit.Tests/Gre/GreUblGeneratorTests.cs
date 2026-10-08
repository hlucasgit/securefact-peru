using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using SecureFact.CpeEngine;
using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;
using SecureFact.Unit.Tests.CpeEngine;

namespace SecureFact.Unit.Tests.Gre;

/// <summary>The XML of the guide of the sender is checked against the official UBL 2.1 schema (S30, identical to the one of S17) and against the tags of the sheet «Guía-Remitente2_0» (S27).</summary>
public class GreUblGeneratorTests
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Root = "urn:oasis:names:specification:ubl:schema:xsd:DespatchAdvice-2";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureFact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static GreXmlData Data(CreateGreRequest request, long number = 1) =>
        new(GreSamples.SenderRuc, "EMISORA DEMO SAC", "T001", number, GreSamples.Today, new TimeOnly(10, 30, 5), request, GreSamples.DocumentNames);

    private static XDocument Build(CreateGreRequest request) => XDocument.Parse(GreUblGenerator.Generate(Data(request)));

    private static List<string> SchemaErrors(XDocument document)
    {
        var withSignature = new XDocument(document);
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
        return Validate(withSignature);
    }

    private static List<string> Validate(XDocument document)
    {
        var assets = Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "xsd", "2.1");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(Root.NamespaceName, Path.Combine(assets, "maindoc", "UBL-DespatchAdvice-2.1.xsd"));
        using var dsig = XmlReader.Create(
            Path.Combine(assets, "common", "UBL-xmldsig-core-schema-2.1.xsd"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });
        schemas.Add(Ds.NamespaceName, dsig);
        var errors = new List<string>();
        document.Validate(schemas, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    private static string Text(XDocument document, string path)
    {
        var manager = new XmlNamespaceManager(new NameTable());
        manager.AddNamespace("cac", Cac.NamespaceName);
        manager.AddNamespace("cbc", Cbc.NamespaceName);
        manager.AddNamespace("d", Root.NamespaceName);
        return document.XPathSelectElement(path, manager)?.Value ?? string.Empty;
    }

    private static string? Attribute(XDocument document, string path, string name)
    {
        var manager = new XmlNamespaceManager(new NameTable());
        manager.AddNamespace("cac", Cac.NamespaceName);
        manager.AddNamespace("cbc", Cbc.NamespaceName);
        manager.AddNamespace("d", Root.NamespaceName);
        return document.XPathSelectElement(path, manager)?.Attribute(name)?.Value;
    }

    [Fact]
    public void A_sale_in_private_transport_validates_against_the_official_schema()
    {
        Assert.Empty(SchemaErrors(Build(GreSamples.PrivateSale())));
    }

    [Fact]
    public void A_sale_in_public_transport_validates_against_the_official_schema()
    {
        Assert.Empty(SchemaErrors(Build(GreSamples.PublicSale())));
    }

    [Fact]
    public void Every_variant_of_the_guide_validates_against_the_official_schema()
    {
        var sale = GreSamples.PrivateSale();
        var variants = new[]
        {
            sale with { VehicleCategoryM1OrL = true, Vehicle = null, Driver = null, PlannedTransshipment = true, ReturnWithEmptyPackaging = true, ReturnEmptyVehicle = true },
            sale with { MotiveCode = "13", MotiveDescription = "Préstamo de equipos para una feria" },
            sale with
            {
                MotiveCode = "03",
                Buyer = new GrePartyInput("6", GreSamples.CarrierRuc, "COMPRADOR SAC"),
                Supplier = null,
                Note = null,
                PackageCount = null,
            },
            sale with
            {
                MotiveCode = "02",
                Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC"),
                Supplier = new GrePartyInput("1", "12345678", "JUAN PEREZ"),
                RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-9", null), new GreRelatedDocumentInput("09", "T001-5", GreSamples.SenderRuc)],
            },
            sale with
            {
                MotiveCode = "04",
                Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC"),
                Origin = new GreAddressInput("150101", "Av. Argentina 123", GreSamples.SenderRuc, "0000"),
                Destination = new GreAddressInput("040101", "Calle Mercaderes 55, Arequipa", GreSamples.SenderRuc, "0001"),
                RelatedDocuments = null,
            },
            sale with
            {
                SecondaryVehicles = [new GreVehicleInput("DEF456", "ABCDE12345"), new GreVehicleInput("GHI789")],
                SecondaryDrivers = [new GreDriverInput("1", "87654321", "ANA", "RUIZ", "Q87654321")],
                Goods = [sale.Goods[0], new GreGoodInput("Cable de cobre", "KGM", 0.5m), new GreGoodInput("Lote", "BX", 1234567.1234567891m, null, null, null)],
                WeightUnit = "TNE",
                GrossWeight = 1.234m,
            },
        };

        foreach (var variant in variants)
        {
            Assert.Empty(SchemaErrors(Build(variant)));
        }
    }

    [Fact]
    public void The_identity_of_the_guide_follows_the_sheet()
    {
        var document = Build(GreSamples.PrivateSale());

        Assert.Equal("2.1", Text(document, "/d:DespatchAdvice/cbc:UBLVersionID"));
        Assert.Equal("2.0", Text(document, "/d:DespatchAdvice/cbc:CustomizationID"));
        Assert.Equal("T001-1", Text(document, "/d:DespatchAdvice/cbc:ID"));
        Assert.Equal("2026-10-08", Text(document, "/d:DespatchAdvice/cbc:IssueDate"));
        Assert.Equal("10:30:05", Text(document, "/d:DespatchAdvice/cbc:IssueTime"));
        Assert.Equal("09", Text(document, "/d:DespatchAdvice/cbc:DespatchAdviceTypeCode"));
        Assert.Equal("urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo01", Attribute(document, "/d:DespatchAdvice/cbc:DespatchAdviceTypeCode", "listURI"));
        Assert.Equal("Entrega en almacén", Text(document, "/d:DespatchAdvice/cbc:Note"));
        Assert.Single(document.Descendants(Ext + "ExtensionContent"));
        Assert.Empty(document.Descendants(Ext + "ExtensionContent").Single().Elements());
    }

    [Fact]
    public void The_serial_number_has_no_leading_zeros()
    {
        var document = XDocument.Parse(GreUblGenerator.Generate(Data(GreSamples.PrivateSale(), number: 12345678)));

        Assert.Equal("T001-12345678", Text(document, "/d:DespatchAdvice/cbc:ID"));
    }

    [Fact]
    public void The_parties_carry_their_document_with_the_scheme_attributes_of_the_sheet()
    {
        var document = Build(GreSamples.PrivateSale() with { Buyer = new GrePartyInput("6", GreSamples.CarrierRuc, "COMPRADOR SAC"), MotiveCode = "03" });

        Assert.Equal("6", Attribute(document, "/d:DespatchAdvice/cac:DespatchSupplierParty/cac:Party/cac:PartyIdentification/cbc:ID", "schemeID"));
        Assert.Equal(GreSamples.SenderRuc, Text(document, "/d:DespatchAdvice/cac:DespatchSupplierParty/cac:Party/cac:PartyIdentification/cbc:ID"));
        Assert.Equal("EMISORA DEMO SAC", Text(document, "/d:DespatchAdvice/cac:DespatchSupplierParty/cac:Party/cac:PartyLegalEntity/cbc:RegistrationName"));
        Assert.Equal("Documento de Identidad", Attribute(document, "/d:DespatchAdvice/cac:DeliveryCustomerParty/cac:Party/cac:PartyIdentification/cbc:ID", "schemeName"));
        Assert.Equal("PE:SUNAT", Attribute(document, "/d:DespatchAdvice/cac:DeliveryCustomerParty/cac:Party/cac:PartyIdentification/cbc:ID", "schemeAgencyName"));
        Assert.Equal("urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo06", Attribute(document, "/d:DespatchAdvice/cac:DeliveryCustomerParty/cac:Party/cac:PartyIdentification/cbc:ID", "schemeURI"));
        Assert.Equal(GreSamples.CustomerRuc, Text(document, "/d:DespatchAdvice/cac:DeliveryCustomerParty/cac:Party/cac:PartyIdentification/cbc:ID"));
        Assert.Equal("COMPRADOR SAC", Text(document, "/d:DespatchAdvice/cac:BuyerCustomerParty/cac:Party/cac:PartyLegalEntity/cbc:RegistrationName"));
    }

    [Fact]
    public void The_shipment_says_the_motive_the_modality_the_weight_and_the_indicators()
    {
        var sale = GreSamples.PrivateSale() with { PlannedTransshipment = true, ReturnEmptyVehicle = true };
        var document = Build(sale);

        Assert.Equal("SUNAT_Envio", Text(document, "/d:DespatchAdvice/cac:Shipment/cbc:ID"));
        Assert.Equal("01", Text(document, "/d:DespatchAdvice/cac:Shipment/cbc:HandlingCode"));
        Assert.Equal("urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo20", Attribute(document, "/d:DespatchAdvice/cac:Shipment/cbc:HandlingCode", "listURI"));
        Assert.Equal("12.5", Text(document, "/d:DespatchAdvice/cac:Shipment/cbc:GrossWeightMeasure"));
        Assert.Equal("KGM", Attribute(document, "/d:DespatchAdvice/cac:Shipment/cbc:GrossWeightMeasure", "unitCode"));
        Assert.Equal("3", Text(document, "/d:DespatchAdvice/cac:Shipment/cbc:TotalTransportHandlingUnitQuantity"));
        Assert.Equal("02", Text(document, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cbc:TransportModeCode"));
        Assert.Equal("2026-10-08", Text(document, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cac:TransitPeriod/cbc:StartDate"));
        var indicators = document.Descendants(Cbc + "SpecialInstructions").Select(e => e.Value).ToList();
        Assert.Equal(["SUNAT_Envio_IndicadorTransbordoProgramado", "SUNAT_Envio_IndicadorRetornoVehiculoVacio"], indicators);
        Assert.Empty(document.Descendants(Cbc + "HandlingInstructions"));
    }

    [Fact]
    public void The_motive_others_carries_its_description()
    {
        var document = Build(GreSamples.PrivateSale() with { MotiveCode = "13", MotiveDescription = "Préstamo de equipos" });

        Assert.Equal("Préstamo de equipos", Text(document, "/d:DespatchAdvice/cac:Shipment/cbc:HandlingInstructions"));
    }

    [Fact]
    public void Private_transport_names_the_vehicle_and_the_driver_and_public_transport_the_carrier_and_the_handover()
    {
        var privateDoc = Build(GreSamples.PrivateSale() with
        {
            SecondaryVehicles = [new GreVehicleInput("def456")],
            SecondaryDrivers = [new GreDriverInput("1", "87654321", "ANA", "RUIZ", "q87654321")],
        });

        Assert.Equal("ABC123", Text(privateDoc, "/d:DespatchAdvice/cac:Shipment/cac:TransportHandlingUnit/cac:TransportEquipment/cbc:ID"));
        Assert.Equal("1234567890", Text(privateDoc, "/d:DespatchAdvice/cac:Shipment/cac:TransportHandlingUnit/cac:TransportEquipment/cac:ApplicableTransportMeans/cbc:RegistrationNationalityID"));
        Assert.Equal("DEF456", Text(privateDoc, "/d:DespatchAdvice/cac:Shipment/cac:TransportHandlingUnit/cac:TransportEquipment/cac:AttachedTransportEquipment/cbc:ID"));
        var drivers = privateDoc.Descendants(Cac + "DriverPerson").ToList();
        Assert.Equal(["Principal", "Secundario"], drivers.Select(d => d.Element(Cbc + "JobTitle")!.Value));
        Assert.Equal(["Q12345678", "Q87654321"], drivers.Select(d => d.Element(Cac + "IdentityDocumentReference")!.Element(Cbc + "ID")!.Value));
        Assert.Equal("JUAN CARLOS", drivers[0].Element(Cbc + "FirstName")!.Value);
        Assert.Equal("PEREZ GOMEZ", drivers[0].Element(Cbc + "FamilyName")!.Value);
        Assert.Empty(privateDoc.Descendants(Cac + "CarrierParty"));

        var publicDoc = Build(GreSamples.PublicSale());
        Assert.Equal("01", Text(publicDoc, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cbc:TransportModeCode"));
        Assert.Equal(GreSamples.CarrierRuc, Text(publicDoc, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cac:CarrierParty/cac:PartyIdentification/cbc:ID"));
        Assert.Equal("TRANSPORTES RAPIDOS SAC", Text(publicDoc, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cac:CarrierParty/cac:PartyLegalEntity/cbc:RegistrationName"));
        Assert.Equal("MTC123456", Text(publicDoc, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cac:CarrierParty/cac:PartyLegalEntity/cbc:CompanyID"));
        Assert.Equal("2026-10-08", Text(publicDoc, "/d:DespatchAdvice/cac:Shipment/cac:ShipmentStage/cac:LoadingTransportEvent/cbc:OccurrenceDate"));
        Assert.Empty(publicDoc.Descendants(Cac + "TransitPeriod"));
        Assert.Empty(publicDoc.Descendants(Cac + "DriverPerson"));
        Assert.Empty(publicDoc.Descendants(Cac + "TransportHandlingUnit"));
    }

    [Fact]
    public void The_points_carry_the_ubigeo_the_address_and_the_establishment_of_the_ruc()
    {
        var document = Build(GreSamples.PrivateSale() with
        {
            Origin = new GreAddressInput("150101", "Av. Argentina 123", GreSamples.SenderRuc, "0000"),
        });

        var start = "/d:DespatchAdvice/cac:Shipment/cac:Delivery/cac:Despatch/cac:DespatchAddress";
        Assert.Equal("150101", Text(document, $"{start}/cbc:ID"));
        Assert.Equal("Ubigeos", Attribute(document, $"{start}/cbc:ID", "schemeName"));
        Assert.Equal("PE:INEI", Attribute(document, $"{start}/cbc:ID", "schemeAgencyName"));
        Assert.Equal("0000", Text(document, $"{start}/cbc:AddressTypeCode"));
        Assert.Equal(GreSamples.SenderRuc, Attribute(document, $"{start}/cbc:AddressTypeCode", "listID"));
        Assert.Equal("Establecimientos anexos", Attribute(document, $"{start}/cbc:AddressTypeCode", "listName"));
        Assert.Equal("Av. Argentina 123", Text(document, $"{start}/cac:AddressLine/cbc:Line"));
        var arrival = "/d:DespatchAdvice/cac:Shipment/cac:Delivery/cac:DeliveryAddress";
        Assert.Equal("150122", Text(document, $"{arrival}/cbc:ID"));
        Assert.Empty(document.XPathSelectElements($"{arrival}/cbc:AddressTypeCode", Manager()));
    }

    private static XmlNamespaceManager Manager()
    {
        var manager = new XmlNamespaceManager(new NameTable());
        manager.AddNamespace("cac", Cac.NamespaceName);
        manager.AddNamespace("cbc", Cbc.NamespaceName);
        manager.AddNamespace("d", Root.NamespaceName);
        return manager;
    }

    [Fact]
    public void The_goods_are_numbered_and_carry_their_unit_and_codes_in_the_order_of_the_schema()
    {
        var sale = GreSamples.PrivateSale() with { Goods = [GreSamples.PrivateSale().Goods[0], new GreGoodInput("Cable de cobre", "KGM", 0.5m)] };
        var document = Build(sale);

        var lines = document.Descendants(Cac + "DespatchLine").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(["1", "2"], lines.Select(l => l.Element(Cbc + "ID")!.Value));
        Assert.Equal(["1", "2"], lines.Select(l => l.Element(Cac + "OrderLineReference")!.Element(Cbc + "LineID")!.Value));
        Assert.Equal("3", lines[0].Element(Cbc + "DeliveredQuantity")!.Value);
        Assert.Equal("NIU", lines[0].Element(Cbc + "DeliveredQuantity")!.Attribute("unitCode")!.Value);
        Assert.Equal("UN/ECE rec 20", lines[0].Element(Cbc + "DeliveredQuantity")!.Attribute("unitCodeListID")!.Value);
        Assert.Equal("0.5", lines[1].Element(Cbc + "DeliveredQuantity")!.Value);
        var item = lines[0].Element(Cac + "Item")!;
        Assert.Equal("Caja de repuestos", item.Element(Cbc + "Description")!.Value);
        Assert.Equal("REP-01", item.Element(Cac + "SellersItemIdentification")!.Element(Cbc + "ID")!.Value);
        Assert.Equal("7750000000011", item.Element(Cac + "StandardItemIdentification")!.Element(Cbc + "ID")!.Value);
        Assert.Equal("27112100", item.Element(Cac + "CommodityClassification")!.Element(Cbc + "ItemClassificationCode")!.Value);
        Assert.Equal("UNSPSC", item.Element(Cac + "CommodityClassification")!.Element(Cbc + "ItemClassificationCode")!.Attribute("listID")!.Value);
    }

    [Fact]
    public void The_related_documents_come_with_their_type_their_name_and_their_issuer()
    {
        var document = Build(GreSamples.PrivateSale());

        var related = Assert.Single(document.Descendants(Cac + "AdditionalDocumentReference"));
        Assert.Equal("F001-123", related.Element(Cbc + "ID")!.Value);
        Assert.Equal("01", related.Element(Cbc + "DocumentTypeCode")!.Value);
        Assert.Equal("urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo61", related.Element(Cbc + "DocumentTypeCode")!.Attribute("listURI")!.Value);
        Assert.Equal("Factura", related.Element(Cbc + "DocumentType")!.Value);
        Assert.Equal(GreSamples.SenderRuc, related.Element(Cac + "IssuerParty")!.Element(Cac + "PartyIdentification")!.Element(Cbc + "ID")!.Value);
    }

    [Fact]
    public void The_texts_are_escaped_so_a_name_cannot_break_the_document()
    {
        var sale = GreSamples.PrivateSale() with
        {
            Recipient = new GrePartyInput("6", GreSamples.CustomerRuc, "<b>CLIENTE</b> & HIJOS \"S.A.\""),
            Note = "a < b && c > d",
        };

        var document = Build(sale);

        Assert.Equal("<b>CLIENTE</b> & HIJOS \"S.A.\"", Text(document, "/d:DespatchAdvice/cac:DeliveryCustomerParty/cac:Party/cac:PartyLegalEntity/cbc:RegistrationName"));
        Assert.Equal("a < b && c > d", Text(document, "/d:DespatchAdvice/cbc:Note"));
        Assert.Empty(SchemaErrors(document));
    }

    [Fact]
    public void The_signer_signs_it_and_the_signed_document_is_still_valid()
    {
        using var certificate = SigningTests.NewCertificate();
        var signer = new XmlDsigSigner();

        var signed = signer.Sign(GreUblGenerator.Generate(Data(GreSamples.PrivateSale())), certificate);

        Assert.True(signed.IsSuccess, signed.IsSuccess ? null : signed.Error.Detail);
        Assert.Empty(Validate(XDocument.Parse(signed.Value.Xml)));
        Assert.True(signer.Verify(signed.Value.Xml).Value.IsValid);
    }
}
