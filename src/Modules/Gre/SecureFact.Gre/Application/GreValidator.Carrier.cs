using System.Text.RegularExpressions;
using SecureFact.Gre.Contracts;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Gre.Application;

/// <summary>
/// The rules of the sheet «Guía-Transportista2_0» of S27 for the guide of the carrier (type 31), in the same way as the ones of the sender (R-069 to R-072). The carrier is the company, and
/// <see cref="GreValidationContext.SenderRuc"/> holds its RUC. What SUNAT answers from its registers (taxpayers, RENIEC, plates, licences, MTC registrations, customs) is left to the CDR.
/// </summary>
internal static partial class GreValidator
{
    /// <summary>The related documents that the first delivery of the guide of the carrier accepts: invoice, receipt, purchase settlement, guide of the sender, foreign document and tax voucher of the transport.</summary>
    private static readonly HashSet<string> CarrierDocumentTypes = new(["01", "03", "04", "09", "12", "48"], StringComparer.Ordinal);

    [GeneratedRegex("^T[A-Z0-9]{3}-[0-9]{1,8}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SenderGuideNumber();

    /// <returns>Every problem found, each with the rule it comes from; empty when the request can be issued.</returns>
    public static IReadOnlyList<string> ValidateCarrier(CreateGreCarrierRequest request, GreValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<string>();
        void Add(string rule, string message) => issues.Add($"[{rule}] {message}");

        var issueDate = request.IssueDate ?? context.Today;
        DateAndNote(issueDate, request.Note, context, Add);
        if (request.MtcRegistration is { Length: > 0 } registration && !MtcRegistrationPattern().IsMatch(registration.Trim().ToUpperInvariant()))
        {
            Add("4392", "El registro MTC del transportista tiene hasta 20 letras mayúsculas y dígitos.");
        }

        CarrierParties(request, context, Add);
        var hasSenderGuide = CarrierRelatedDocuments(request, context, Add);
        Weight(request.GrossWeight, request.WeightUnit, request.PackageCount, Add);
        if (request.TransferStartDate < issueDate)
        {
            Add("3343", "La fecha de inicio del traslado no puede ser anterior a la de emisión.");
        }

        CarrierPoints(request, hasSenderGuide, Add);
        CarrierTransport(request, Add);
        CarrierGoods(request, context, hasSenderGuide, Add);
        CarrierIndicators(request, context, Add);
        return issues;
    }

    private static void CarrierParties(CreateGreCarrierRequest request, GreValidationContext context, Action<string, string> add)
    {
        if (request.Sender is null)
        {
            add("3383", "Falta el remitente (quien envía los bienes).");
        }
        else
        {
            Party("remitente", request.Sender, "3383", add);
            if (request.Sender.DocumentTypeCode?.Trim() is { Length: > 0 } type && !context.Catalogs.DocumentTypes.Contains(type))
            {
                add("2542", "El tipo de documento del remitente no está en el catálogo 06.");
            }

            if (SameParty(request.Sender, "6", context.SenderRuc))
            {
                add("2560", "El remitente no puede ser el propio transportista.");
            }
        }

        if (request.Recipient is null)
        {
            add("2757", "Falta el destinatario.");
            return;
        }

        Party("destinatario", request.Recipient, "2757", add);
        if (request.Recipient.DocumentTypeCode?.Trim() is { Length: > 0 } recipientType && !context.Catalogs.DocumentTypes.Contains(recipientType))
        {
            add("2760", "El tipo de documento del destinatario no está en el catálogo 06.");
        }
    }

    /// <returns>Whether a guide of the sender (09, series «T…») is related: it lists the goods and the addresses, so those are not asked again.</returns>
    private static bool CarrierRelatedDocuments(CreateGreCarrierRequest request, GreValidationContext context, Action<string, string> add)
    {
        var documents = request.RelatedDocuments ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasSenderGuide = false;
        foreach (var document in documents)
        {
            var type = document.TypeCode?.Trim() ?? string.Empty;
            var number = document.Number?.Trim() ?? string.Empty;
            if (!context.Catalogs.RelatedDocumentApplicability.TryGetValue(type, out var applicability) || !applicability.Contains("transportista", StringComparison.Ordinal))
            {
                add("2692", $"El documento relacionado «{type}» no está en el catálogo 61 para la guía del transportista.");
                continue;
            }

            if (!CarrierDocumentTypes.Contains(type))
            {
                add("3445", $"El documento relacionado «{type}» (aduanas, eventos, constancias u otras guías) aún no se admite aquí.");
                continue;
            }

            if (number.Length == 0)
            {
                add("3403", $"Falta el número del documento relacionado «{type}».");
                continue;
            }

            if (!seen.Add($"{type}|{number}"))
            {
                add("3340", $"El documento relacionado «{type}» {number} se repite.");
            }

            if (!RelatedNumberIsValid(type, number))
            {
                add("3441", $"El número «{number}» no tiene la forma del documento relacionado «{type}».");
            }

            if (type == "09")
            {
                if (SenderGuideNumber().IsMatch(number))
                {
                    hasSenderGuide = true;
                }
                else
                {
                    add("3445", "De la guía del remitente (09) solo se admiten las electrónicas, con serie «T…»; las de serie numérica (impresas) aún no se admiten aquí.");
                }
            }

            var issuer = document.IssuerRuc?.Trim() ?? string.Empty;
            if (issuer.Length == 0)
            {
                add("3380", $"Falta el RUC del emisor del documento relacionado «{type}».");
            }
            else if (!Ruc.Create(issuer).IsSuccess)
            {
                add("3409", $"El RUC del emisor del documento relacionado «{type}» no es válido.");
            }
            else if (type == "09" && request.Sender is { DocumentTypeCode: "6" } sender && issuer != sender.DocumentNumber?.Trim())
            {
                add("3381", "El emisor de la guía relacionada «09» es el remitente de esta guía.");
            }
        }

        if (!hasSenderGuide && documents.Count > 1)
        {
            add("3346", "Sin una guía del remitente (09) como documento relacionado, solo se admite un documento relacionado.");
        }

        return hasSenderGuide;
    }

    private static void CarrierPoints(CreateGreCarrierRequest request, bool hasSenderGuide, Action<string, string> add)
    {
        var addressRequired = !hasSenderGuide && !request.PlannedTransshipment;
        CarrierPoint("partida", request.Origin, addressRequired, "2577", add);
        CarrierPoint("llegada", request.Destination, addressRequired, "2574", add);
    }

    private static void CarrierPoint(string label, GreAddressInput? address, bool addressRequired, string addressRule, Action<string, string> add)
    {
        if (address is null)
        {
            add("2775", $"Falta el punto de {label}.");
            return;
        }

        if (!UbigeoPattern().IsMatch(address.UbigeoCode?.Trim() ?? string.Empty))
        {
            add("2776", $"El ubigeo del punto de {label} tiene 6 dígitos.");
        }

        var text = address.Address?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            if (addressRequired)
            {
                add(addressRule, $"Falta la dirección del punto de {label}.");
            }
        }
        else if (!IsPlainText(text, 3, 500))
        {
            add("4076", $"La dirección del punto de {label} tiene de 3 a 500 caracteres, sin saltos de línea.");
        }
    }

    private static void CarrierTransport(CreateGreCarrierRequest request, Action<string, string> add)
    {
        if (request.Vehicle is null)
        {
            add("2566", "Hay que informar el vehículo principal (placa).");
        }
        else
        {
            CarrierVehicle(request.Vehicle, "principal", add);
        }

        var secondaries = request.SecondaryVehicles ?? [];
        if (secondaries.Count > 2)
        {
            add("4389", "Se admiten hasta 2 vehículos secundarios.");
        }

        foreach (var vehicle in secondaries)
        {
            CarrierVehicle(vehicle, "secundario", add);
        }

        if (request.Driver is null)
        {
            add("3357", "Hay que informar al conductor principal.");
        }
        else
        {
            DriverRules(request.Driver, "principal", add);
        }

        var secondaryDrivers = request.SecondaryDrivers ?? [];
        if (secondaryDrivers.Count > 2)
        {
            add("4376", "Se admiten hasta 2 conductores secundarios.");
        }

        foreach (var driver in secondaryDrivers)
        {
            DriverRules(driver, "secundario", add);
        }

        var licences = secondaryDrivers.Select(d => d.LicenseNumber?.Trim().ToUpperInvariant()).Where(l => !string.IsNullOrEmpty(l)).ToList();
        if (licences.Count != licences.Distinct(StringComparer.Ordinal).Count())
        {
            add("3362", "Dos conductores secundarios no pueden tener la misma licencia.");
        }
    }

    /// <summary>The vehicle of a carrier has its circulation card or enabling certificate (4399).</summary>
    private static void CarrierVehicle(GreVehicleInput vehicle, string kind, Action<string, string> add)
    {
        VehicleRules(vehicle, kind, add);
        if (string.IsNullOrWhiteSpace(vehicle.CirculationCard))
        {
            add("4399", $"Falta la tarjeta de circulación o el certificado de habilitación del vehículo {kind}.");
        }
    }

    private static void CarrierGoods(CreateGreCarrierRequest request, GreValidationContext context, bool hasSenderGuide, Action<string, string> add)
    {
        var goods = request.Goods ?? [];
        if (hasSenderGuide)
        {
            if (goods.Count > 0)
            {
                add("4434", "Con una guía del remitente (09) como documento relacionado los bienes los lista esa guía: no se informan aquí.");
            }

            return;
        }

        if (goods.Count == 0)
        {
            add("3435", "Hay que informar al menos un bien a transportar, o relacionar una guía del remitente (09) que ya los lista.");
        }

        Goods(goods, context, add, required: false);
    }

    private static void CarrierIndicators(CreateGreCarrierRequest request, GreValidationContext context, Action<string, string> add)
    {
        if (request.Subcontracted)
        {
            if (request.Subcontractor is not { } subcontractor)
            {
                add("4424", "Con transporte subcontratado hay que informar al subcontratador (RUC y razón social).");
            }
            else
            {
                if (subcontractor.DocumentTypeCode?.Trim() != "6")
                {
                    add("3391", "El subcontratador se identifica con RUC.");
                }
                else if (!Ruc.Create(subcontractor.DocumentNumber?.Trim() ?? string.Empty).IsSuccess)
                {
                    add("4424", "El RUC del subcontratador no es válido.");
                }
                else if (subcontractor.DocumentNumber?.Trim() == context.SenderRuc)
                {
                    add("3390", "El subcontratador no puede ser el propio transportista.");
                }

                if (!IsPlainText(subcontractor.Name ?? string.Empty, 1, 250))
                {
                    add("4426", "La razón social del subcontratador es obligatoria (hasta 250 caracteres, sin saltos de línea).");
                }
            }
        }
        else if (request.Subcontractor is not null)
        {
            add("4424", "El subcontratador solo se informa con el transporte subcontratado.");
        }

        if (request.FreightPayer == GreFreightPayer.ThirdParty)
        {
            if (request.ThirdPartyPayer is not { } payer)
            {
                add("4402", "Cuando paga un tercero hay que informar quién es (documento y nombre).");
            }
            else
            {
                Party("tercero que paga el flete", payer, "3400", add);
                if (payer.DocumentTypeCode?.Trim() is { Length: > 0 } type && !context.Catalogs.DocumentTypes.Contains(type))
                {
                    add("3399", "El tipo de documento de quien paga el flete no está en el catálogo 06.");
                }
            }
        }
        else if (request.ThirdPartyPayer is not null)
        {
            add("4402", "Quien paga el flete solo se informa cuando paga un tercero.");
        }
    }
}
