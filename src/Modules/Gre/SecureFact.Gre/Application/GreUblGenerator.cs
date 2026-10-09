using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SecureFact.Gre.Contracts;

namespace SecureFact.Gre.Application;

/// <summary>Everything the XML of a guide of the sender is made of: the company, the number, the moment and the validated request.</summary>
/// <param name="RelatedDocumentNames">The names of catalogue 61 by code, for the description of each related document.</param>
internal sealed record GreXmlData(
    string SenderRuc,
    string SenderName,
    string Series,
    long Number,
    DateOnly IssueDate,
    TimeOnly IssueTime,
    CreateGreRequest Request,
    IReadOnlyDictionary<string, string> RelatedDocumentNames);

/// <summary>
/// The <c>DespatchAdvice</c> UBL 2.1 of the guide of the sender (type 09, <c>CustomizationID</c> 2.0), built as the tags of the sheet «Guía-Remitente2_0» of S27 say (R-062 to R-066) and
/// in the order of the schema (S30). The result is unsigned: the signer fills the <c>ext:ExtensionContent</c>. The guide of the carrier (type 31) is not built here (R-068).
/// </summary>
internal static partial class GreUblGenerator
{
    private static readonly XNamespace Root = "urn:oasis:names:specification:ubl:schema:xsd:DespatchAdvice-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    private const string Agency = "PE:SUNAT";
    private const string CatalogueUri = "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo";

    public static string Generate(GreXmlData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var request = data.Request;
        var root = new XElement(
            Root + "DespatchAdvice",
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ext", Ext),
            new XAttribute(XNamespace.Xmlns + "ds", Ds),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.1"),
            new XElement(Cbc + "CustomizationID", "2.0"),
            new XElement(Cbc + "ID", $"{data.Series}-{data.Number.ToString(CultureInfo.InvariantCulture)}"),
            new XElement(Cbc + "IssueDate", Date(data.IssueDate)),
            new XElement(Cbc + "IssueTime", data.IssueTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(
                Cbc + "DespatchAdviceTypeCode",
                new XAttribute("listAgencyName", Agency),
                new XAttribute("listName", "Tipo de Documento"),
                new XAttribute("listURI", CatalogueUri + "01"),
                "09"));

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            root.Add(new XElement(Cbc + "Note", request.Note.Trim()));
        }

        foreach (var document in request.RelatedDocuments ?? [])
        {
            root.Add(RelatedDocument(document, data.RelatedDocumentNames));
        }

        root.Add(
            SignatureInfo(data.SenderRuc, data.SenderName),
            new XElement(Cac + "DespatchSupplierParty", Party("6", data.SenderRuc, data.SenderName)),
            new XElement(Cac + "DeliveryCustomerParty", Party(request.Recipient.DocumentTypeCode.Trim(), request.Recipient.DocumentNumber.Trim(), request.Recipient.Name.Trim())));
        if (request.Buyer is { } buyer)
        {
            root.Add(new XElement(Cac + "BuyerCustomerParty", Party(buyer.DocumentTypeCode.Trim(), buyer.DocumentNumber.Trim(), buyer.Name.Trim())));
        }

        if (request.Supplier is { } supplier)
        {
            root.Add(new XElement(Cac + "SellerSupplierParty", Party(supplier.DocumentTypeCode.Trim(), supplier.DocumentNumber.Trim(), supplier.Name.Trim())));
        }

        root.Add(Shipment(request));
        for (var i = 0; i < request.Goods.Count; i++)
        {
            root.Add(Line(i + 1, request.Goods[i]));
        }

        return Serialize(root);
    }

    private static string Serialize(XElement root)
    {
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var buffer = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }

        return buffer.ToString();
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static XElement IdentityId(string type, string number) =>
        new(
            Cbc + "ID",
            new XAttribute("schemeID", type),
            new XAttribute("schemeName", "Documento de Identidad"),
            new XAttribute("schemeAgencyName", Agency),
            new XAttribute("schemeURI", CatalogueUri + "06"),
            number);

    private static XElement Party(string type, string number, string name) =>
        new(
            Cac + "Party",
            new XElement(Cac + "PartyIdentification", IdentityId(type, number)),
            new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", name)));

    private static XElement SignatureInfo(string ruc, string name) =>
        new(
            Cac + "Signature",
            new XElement(Cbc + "ID", "IDSignSP"),
            new XElement(
                Cac + "SignatoryParty",
                new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", ruc)),
                new XElement(Cac + "PartyName", new XElement(Cbc + "Name", name))),
            new XElement(Cac + "DigitalSignatureAttachment", new XElement(Cac + "ExternalReference", new XElement(Cbc + "URI", "#SignatureSP"))));

    private static XElement RelatedDocument(GreRelatedDocumentInput document, IReadOnlyDictionary<string, string> names)
    {
        var type = document.TypeCode.Trim();
        var element = new XElement(
            Cac + "AdditionalDocumentReference",
            new XElement(Cbc + "ID", document.Number.Trim()),
            new XElement(
                Cbc + "DocumentTypeCode",
                new XAttribute("listAgencyName", Agency),
                new XAttribute("listName", "Documento relacionado al transporte"),
                new XAttribute("listURI", CatalogueUri + "61"),
                type));
        if (names.TryGetValue(type, out var name) && name.Length > 0)
        {
            element.Add(new XElement(Cbc + "DocumentType", name.Length > 120 ? name[..120] : name));
        }

        if (!string.IsNullOrWhiteSpace(document.IssuerRuc))
        {
            element.Add(new XElement(Cac + "IssuerParty", new XElement(Cac + "PartyIdentification", IdentityId("6", document.IssuerRuc.Trim()))));
        }

        return element;
    }

    private static XElement Shipment(CreateGreRequest request)
    {
        var shipment = new XElement(
            Cac + "Shipment",
            new XElement(Cbc + "ID", "SUNAT_Envio"),
            new XElement(
                Cbc + "HandlingCode",
                new XAttribute("listAgencyName", Agency),
                new XAttribute("listName", "Motivo de traslado"),
                new XAttribute("listURI", CatalogueUri + "20"),
                request.MotiveCode));
        if (request.MotiveCode == "13")
        {
            shipment.Add(new XElement(Cbc + "HandlingInstructions", request.MotiveDescription!.Trim()));
        }

        shipment.Add(new XElement(Cbc + "GrossWeightMeasure", new XAttribute("unitCode", request.WeightUnit), Number(request.GrossWeight)));
        if (request.PackageCount is { } packages)
        {
            shipment.Add(new XElement(Cbc + "TotalTransportHandlingUnitQuantity", packages.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var indicator in Indicators(request))
        {
            shipment.Add(new XElement(Cbc + "SpecialInstructions", indicator));
        }

        shipment.Add(Stage(request), Delivery(request));
        if (Vehicles(request.Vehicle, request.SecondaryVehicles) is { } vehicles)
        {
            shipment.Add(vehicles);
        }

        return shipment;
    }

    private static IEnumerable<string> Indicators(CreateGreRequest request)
    {
        if (request.PlannedTransshipment)
        {
            yield return "SUNAT_Envio_IndicadorTransbordoProgramado";
        }

        if (request.VehicleCategoryM1OrL)
        {
            yield return "SUNAT_Envio_IndicadorTrasladoVehiculoM1L";
        }

        if (request.ReturnWithEmptyPackaging)
        {
            yield return "SUNAT_Envio_IndicadorRetornoVehiculoEnvaseVacio";
        }

        if (request.ReturnEmptyVehicle)
        {
            yield return "SUNAT_Envio_IndicadorRetornoVehiculoVacio";
        }
    }

    private static XElement Stage(CreateGreRequest request)
    {
        var stage = new XElement(
            Cac + "ShipmentStage",
            new XElement(
                Cbc + "TransportModeCode",
                new XAttribute("listName", "Modalidad de traslado"),
                new XAttribute("listAgencyName", Agency),
                new XAttribute("listURI", CatalogueUri + "18"),
                request.ModalityCode));
        if (request.ModalityCode == "02" && request.TransferStartDate is { } start)
        {
            stage.Add(new XElement(Cac + "TransitPeriod", new XElement(Cbc + "StartDate", Date(start))));
        }

        if (request.ModalityCode == "01" && request.Carrier is { } carrier)
        {
            var party = new XElement(
                Cac + "CarrierParty",
                new XElement(Cac + "PartyIdentification", IdentityId("6", carrier.Ruc.Trim())));
            var legal = new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", carrier.Name.Trim()));
            if (!string.IsNullOrWhiteSpace(carrier.MtcRegistration))
            {
                legal.Add(new XElement(Cbc + "CompanyID", carrier.MtcRegistration.Trim().ToUpperInvariant()));
            }

            party.Add(legal);
            stage.Add(party);
        }

        if (request.ModalityCode == "01" && request.HandoverDate is { } handover)
        {
            stage.Add(new XElement(Cac + "LoadingTransportEvent", new XElement(Cbc + "OccurrenceDate", Date(handover))));
        }

        if (request.Driver is { } driver)
        {
            stage.Add(Driver(driver, "Principal"));
        }

        foreach (var secondary in request.SecondaryDrivers ?? [])
        {
            stage.Add(Driver(secondary, "Secundario"));
        }

        return stage;
    }

    private static XElement Driver(GreDriverInput driver, string kind) =>
        new(
            Cac + "DriverPerson",
            IdentityId(driver.DocumentTypeCode.Trim(), driver.DocumentNumber.Trim()),
            new XElement(Cbc + "FirstName", driver.FirstNames.Trim()),
            new XElement(Cbc + "FamilyName", driver.LastNames.Trim()),
            new XElement(Cbc + "JobTitle", kind),
            new XElement(Cac + "IdentityDocumentReference", new XElement(Cbc + "ID", driver.LicenseNumber.Trim().ToUpperInvariant())));

    private static XElement Delivery(CreateGreRequest request) =>
        new(
            Cac + "Delivery",
            Address("DeliveryAddress", request.Destination),
            new XElement(Cac + "Despatch", Address("DespatchAddress", request.Origin)));

    private static XElement Address(string name, GreAddressInput address)
    {
        var element = new XElement(
            Cac + name,
            new XElement(Cbc + "ID", new XAttribute("schemeAgencyName", "PE:INEI"), new XAttribute("schemeName", "Ubigeos"), address.UbigeoCode.Trim()));
        if (!string.IsNullOrWhiteSpace(address.EstablishmentCode))
        {
            element.Add(new XElement(
                Cbc + "AddressTypeCode",
                new XAttribute("listID", address.EstablishmentRuc!.Trim()),
                new XAttribute("listAgencyName", Agency),
                new XAttribute("listName", "Establecimientos anexos"),
                address.EstablishmentCode.Trim()));
        }

        element.Add(new XElement(Cac + "AddressLine", new XElement(Cbc + "Line", address.Address.Trim())));
        return element;
    }

    /// <summary>The vehicles of the private transport: the principal one and up to two attached, in a single transport handling unit.</summary>
    private static XElement? Vehicles(GreVehicleInput? vehicle, IReadOnlyList<GreVehicleInput>? secondaries)
    {
        if (vehicle is null)
        {
            return null;
        }

        var equipment = new XElement(Cac + "TransportEquipment", new XElement(Cbc + "ID", vehicle.Plate.Trim().ToUpperInvariant()));
        if (!string.IsNullOrWhiteSpace(vehicle.CirculationCard))
        {
            equipment.Add(new XElement(Cac + "ApplicableTransportMeans", new XElement(Cbc + "RegistrationNationalityID", vehicle.CirculationCard.Trim().ToUpperInvariant())));
        }

        foreach (var secondary in secondaries ?? [])
        {
            var attached = new XElement(Cac + "AttachedTransportEquipment", new XElement(Cbc + "ID", secondary.Plate.Trim().ToUpperInvariant()));
            if (!string.IsNullOrWhiteSpace(secondary.CirculationCard))
            {
                attached.Add(new XElement(Cac + "ApplicableTransportMeans", new XElement(Cbc + "RegistrationNationalityID", secondary.CirculationCard.Trim().ToUpperInvariant())));
            }

            equipment.Add(attached);
        }

        return new XElement(Cac + "TransportHandlingUnit", equipment);
    }

    private static XElement Line(int order, GreGoodInput good)
    {
        var item = new XElement(Cac + "Item", new XElement(Cbc + "Description", good.Description.Trim()));
        if (!string.IsNullOrWhiteSpace(good.Code))
        {
            item.Add(new XElement(Cac + "SellersItemIdentification", new XElement(Cbc + "ID", good.Code.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(good.Gtin))
        {
            item.Add(new XElement(Cac + "StandardItemIdentification", new XElement(Cbc + "ID", good.Gtin.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(good.SunatProductCode))
        {
            item.Add(new XElement(
                Cac + "CommodityClassification",
                new XElement(
                    Cbc + "ItemClassificationCode",
                    new XAttribute("listID", "UNSPSC"),
                    new XAttribute("listAgencyName", "GS1 US"),
                    new XAttribute("listName", "Item Classification"),
                    good.SunatProductCode.Trim())));
        }

        return new XElement(
            Cac + "DespatchLine",
            new XElement(Cbc + "ID", order.ToString(CultureInfo.InvariantCulture)),
            new XElement(
                Cbc + "DeliveredQuantity",
                new XAttribute("unitCode", good.UnitCode.Trim()),
                new XAttribute("unitCodeListID", "UN/ECE rec 20"),
                new XAttribute("unitCodeListAgencyName", "United Nations Economic Commission for Europe"),
                Number(good.Quantity)),
            new XElement(Cac + "OrderLineReference", new XElement(Cbc + "LineID", order.ToString(CultureInfo.InvariantCulture))),
            item);
    }

    /// <summary>A decimal as the schema writes it: no exponent, the point as separator and no trailing zeros.</summary>
    private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
