using System.Globalization;
using System.Xml.Linq;
using SecureFact.Gre.Contracts;

namespace SecureFact.Gre.Application;

/// <summary>Everything the XML of a guide of the carrier is made of: the company, the number, the moment and the validated request.</summary>
/// <param name="RelatedDocumentNames">The names of catalogue 61 by code, for the description of each related document.</param>
internal sealed record GreCarrierXmlData(
    string CarrierRuc,
    string CarrierName,
    string Series,
    long Number,
    DateOnly IssueDate,
    TimeOnly IssueTime,
    CreateGreCarrierRequest Request,
    IReadOnlyDictionary<string, string> RelatedDocumentNames);

internal static partial class GreUblGenerator
{
    /// <summary>
    /// The <c>DespatchAdvice</c> UBL 2.1 of the guide of the carrier (type 31, <c>CustomizationID</c> 2.0), built as the tags of the sheet «Guía-Transportista2_0» of S27 say (R-069 to R-072) and in
    /// the order of the schema (S30). The result is unsigned: the signer fills the <c>ext:ExtensionContent</c>.
    /// </summary>
    public static string GenerateCarrier(GreCarrierXmlData data)
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
                "31"));

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            root.Add(new XElement(Cbc + "Note", request.Note.Trim()));
        }

        foreach (var document in request.RelatedDocuments ?? [])
        {
            root.Add(RelatedDocument(document, data.RelatedDocumentNames));
        }

        root.Add(
            SignatureInfo(data.CarrierRuc, data.CarrierName),
            new XElement(Cac + "DespatchSupplierParty", Party("6", data.CarrierRuc, data.CarrierName)),
            new XElement(Cac + "DeliveryCustomerParty", Party(request.Recipient.DocumentTypeCode.Trim(), request.Recipient.DocumentNumber.Trim(), request.Recipient.Name.Trim())));
        if (request.FreightPayer == GreFreightPayer.ThirdParty && request.ThirdPartyPayer is { } payer)
        {
            root.Add(new XElement(Cac + "OriginatorCustomerParty", Party(payer.DocumentTypeCode.Trim(), payer.DocumentNumber.Trim(), payer.Name.Trim())));
        }

        root.Add(CarrierShipment(request));
        var order = 1;
        foreach (var good in request.Goods ?? [])
        {
            root.Add(Line(order++, good));
        }

        if (order == 1)
        {
            root.Add(AnnotationLine(request));
        }

        return Serialize(root);
    }

    /// <summary>
    /// The schema asks for at least one line. When the goods are those of the guide of the sender that is related, the line is the optional annotation of the sheet (order number «0», without a quantity),
    /// which says where the goods are listed.
    /// </summary>
    private static XElement AnnotationLine(CreateGreCarrierRequest request)
    {
        var related = (request.RelatedDocuments ?? []).FirstOrDefault(d => d.TypeCode.Trim() == "09")?.Number.Trim();
        var description = related is null ? "Bienes según el documento relacionado" : $"Bienes según la guía de remisión remitente {related}";
        return new XElement(
            Cac + "DespatchLine",
            new XElement(Cbc + "ID", "0"),
            new XElement(Cac + "OrderLineReference", new XElement(Cbc + "LineID", "0")),
            new XElement(Cac + "Item", new XElement(Cbc + "Description", description)));
    }

    private static XElement CarrierShipment(CreateGreCarrierRequest request)
    {
        var shipment = new XElement(
            Cac + "Shipment",
            new XElement(Cbc + "ID", "SUNAT_Envio"),
            new XElement(Cbc + "GrossWeightMeasure", new XAttribute("unitCode", request.WeightUnit), Number(request.GrossWeight)));
        if (request.PackageCount is { } packages)
        {
            shipment.Add(new XElement(Cbc + "TotalTransportHandlingUnitQuantity", packages.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var indicator in CarrierIndicators(request))
        {
            shipment.Add(new XElement(Cbc + "SpecialInstructions", indicator));
        }

        if (request.Subcontracted && request.Subcontractor is { } subcontractor)
        {
            shipment.Add(new XElement(
                Cac + "Consignment",
                new XElement(Cbc + "ID", "SUNAT_Envio"),
                new XElement(
                    Cac + "LogisticsOperatorParty",
                    new XElement(Cac + "PartyIdentification", IdentityId("6", subcontractor.DocumentNumber.Trim())),
                    new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", subcontractor.Name.Trim())))));
        }

        var stage = new XElement(Cac + "ShipmentStage", new XElement(Cac + "TransitPeriod", new XElement(Cbc + "StartDate", Date(request.TransferStartDate))));
        if (!string.IsNullOrWhiteSpace(request.MtcRegistration))
        {
            stage.Add(new XElement(
                Cac + "CarrierParty",
                new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "CompanyID", request.MtcRegistration.Trim().ToUpperInvariant()))));
        }

        stage.Add(Driver(request.Driver, "Principal"));
        foreach (var secondary in request.SecondaryDrivers ?? [])
        {
            stage.Add(Driver(secondary, "Secundario"));
        }

        shipment.Add(stage, CarrierDelivery(request));
        if (Vehicles(request.Vehicle, request.SecondaryVehicles) is { } vehicles)
        {
            shipment.Add(vehicles);
        }

        return shipment;
    }

    private static IEnumerable<string> CarrierIndicators(CreateGreCarrierRequest request)
    {
        if (request.PlannedTransshipment)
        {
            yield return "SUNAT_Envio_IndicadorTransbordoProgramado";
        }

        if (request.ReturnWithEmptyPackaging)
        {
            yield return "SUNAT_Envio_IndicadorRetornoVehiculoEnvaseVacio";
        }

        if (request.ReturnEmptyVehicle)
        {
            yield return "SUNAT_Envio_IndicadorRetornoVehiculoVacio";
        }

        if (request.Subcontracted)
        {
            yield return "SUNAT_Envio_IndicadorTrasporteSubcontratado";
        }

        yield return request.FreightPayer switch
        {
            GreFreightPayer.Subcontractor => "SUNAT_Envio_IndicadorPagadorFlete_Subcontratador",
            GreFreightPayer.ThirdParty => "SUNAT_Envio_IndicadorPagadorFlete_Tercero",
            _ => "SUNAT_Envio_IndicadorPagadorFlete_Remitente",
        };
    }

    /// <summary>The arrival, and the departure with the sender that dispatches the goods.</summary>
    private static XElement CarrierDelivery(CreateGreCarrierRequest request)
    {
        var sender = request.Sender;
        return new XElement(
            Cac + "Delivery",
            CarrierAddress("DeliveryAddress", request.Destination),
            new XElement(
                Cac + "Despatch",
                CarrierAddress("DespatchAddress", request.Origin),
                new XElement(
                    Cac + "DespatchParty",
                    new XElement(Cac + "PartyIdentification", IdentityId(sender.DocumentTypeCode.Trim(), sender.DocumentNumber.Trim())),
                    new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", sender.Name.Trim())))));
    }

    /// <summary>A point of the transfer: the ubigeo and, when it is stated, the address (it is left out when the guide of the sender that is related already says it).</summary>
    private static XElement CarrierAddress(string name, GreAddressInput address)
    {
        var element = new XElement(
            Cac + name,
            new XElement(Cbc + "ID", new XAttribute("schemeAgencyName", "PE:INEI"), new XAttribute("schemeName", "Ubigeos"), address.UbigeoCode.Trim()));
        if (!string.IsNullOrWhiteSpace(address.Address))
        {
            element.Add(new XElement(Cac + "AddressLine", new XElement(Cbc + "Line", address.Address.Trim())));
        }

        return element;
    }
}
