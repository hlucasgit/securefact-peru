using System.Globalization;
using System.Text.RegularExpressions;
using SecureFact.Gre.Contracts;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Gre.Application;

/// <summary>
/// What the validator needs to know that is not in the request: the company, the day, the parameters (rules as data) and the catalogues, already read.
/// </summary>
/// <param name="Catalogs">The codes in force today of catalogues 03, 06, 18, 20 and 61; catalogue 61 also says for which guide each code applies.</param>
internal sealed record GreValidationContext(
    string SenderRuc,
    DateOnly Today,
    int MaxIssueLagDays,
    IReadOnlySet<string> GenericMotiveDescriptions,
    GreCatalogs Catalogs);

internal sealed record GreCatalogs(
    IReadOnlySet<string> UnitCodes,
    IReadOnlySet<string> DocumentTypes,
    IReadOnlySet<string> Modalities,
    IReadOnlySet<string> Motives,
    IReadOnlyDictionary<string, string> RelatedDocumentApplicability);

/// <summary>
/// The rules of the validation workbook of the GRE (S27, sheet «Guía-Remitente2_0») that SUNAT applies to the shape of the file (the ones marked XSL), written as the guide of the
/// sender is built, and the conditional ones by motive and modality (R-062 to R-066). The rules that need SUNAT's registers (taxpayer, RENIEC, plates, licences, annexed establishments,
/// DAM) are left to SUNAT, which answers them in the CDR. Each message carries the code of the rule it comes from.
/// </summary>
internal static partial class GreValidator
{
    /// <summary>Motives that the first delivery issues: 08, 09 and 19 need customs documents, ports and containers, and 18 is the itinerant issuer (R-068).</summary>
    public static readonly IReadOnlySet<string> SupportedMotives = new HashSet<string>(["01", "02", "03", "04", "05", "06", "07", "13", "14", "17"], StringComparer.Ordinal);

    /// <summary>Related documents of customs and ports, which belong to the motives not supported yet.</summary>
    private static readonly HashSet<string> CustomsDocuments = new(["50", "52", "91", "92"], StringComparer.Ordinal);

    private static readonly HashSet<string> RecipientIsSender = new(["02", "04", "07"], StringComparer.Ordinal);
    private static readonly HashSet<string> RecipientIsNotSender = new(["01", "03", "05", "06", "14", "17"], StringComparer.Ordinal);
    private static readonly HashSet<string> RelatedDocumentsWithIssuer = new(["01", "03", "04", "09", "12", "48", "92"], StringComparer.Ordinal);

    [GeneratedRegex("^[A-Z0-9]{1,3}$")]
    private static partial Regex UnitShape();

    [GeneratedRegex("^[A-Z0-9]{6,8}$")]
    private static partial Regex PlatePattern();

    [GeneratedRegex("^[A-Z0-9]{10,15}$")]
    private static partial Regex CirculationCardPattern();

    [GeneratedRegex("^[A-Z0-9]{9,10}$")]
    private static partial Regex LicencePattern();

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex UbigeoPattern();

    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex EstablishmentPattern();

    [GeneratedRegex("^[0-9]{1,8}$")]
    private static partial Regex SunatProductPattern();

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex MtcRegistrationPattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,14}$")]
    private static partial Regex GtinPattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,15}$")]
    private static partial Regex ForeignDocumentPattern();

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex DniPattern();

    /// <returns>Every problem found, each with the rule it comes from; empty when the request can be issued.</returns>
    public static IReadOnlyList<string> Validate(CreateGreRequest request, GreValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<string>();
        void Add(string rule, string message) => issues.Add($"[{rule}] {message}");

        var issueDate = request.IssueDate ?? context.Today;
        Header(request, context, issueDate, Add);
        Parties(request, context, Add);
        Shipment(request, issueDate, Add);
        Transport(request, Add);
        Points(request, context, request.MotiveCode ?? string.Empty, Add);
        Goods(request.Goods, context, Add);
        RelatedDocuments(request, context, request.Recipient, Add);
        return issues;
    }

    // ---------- header ----------

    /// <summary>The rules of the date of issue and of the note, which the guides of the sender and of the carrier share.</summary>
    private static void DateAndNote(DateOnly issueDate, string? note, GreValidationContext context, Action<string, string> add)
    {
        if (issueDate > context.Today)
        {
            add("2329", "La fecha de emisión no puede ser posterior a hoy.");
        }
        else if (context.Today.DayNumber - issueDate.DayNumber > context.MaxIssueLagDays)
        {
            add("2108", $"La fecha de emisión no puede ser anterior en más de {context.MaxIssueLagDays} día(s) a la de envío.");
        }

        if (note is not null && !IsPlainText(note, 1, 250))
        {
            add("4186", "Las observaciones admiten hasta 250 caracteres, sin saltos de línea ni tabulaciones.");
        }
    }

    private static void Header(CreateGreRequest request, GreValidationContext context, DateOnly issueDate, Action<string, string> add)
    {
        DateAndNote(issueDate, request.Note, context, add);

        if (!context.Catalogs.Motives.Contains(request.MotiveCode ?? string.Empty))
        {
            add("3405", "El motivo de traslado no está en el catálogo 20.");
        }

        if (!context.Catalogs.Modalities.Contains(request.ModalityCode ?? string.Empty))
        {
            add("2773", "La modalidad de traslado no está en el catálogo 18.");
        }

        if (request.MotiveCode == "13")
        {
            var description = request.MotiveDescription?.Trim() ?? string.Empty;
            if (description.Length == 0)
            {
                add("3457", "El motivo «otros» (13) exige describir el traslado.");
            }
            else if (!IsPlainText(description, 3, 100) || description.Count(char.IsLetter) < 3)
            {
                add("4190", "La descripción del motivo «otros» tiene de 3 a 100 caracteres, con al menos 3 letras.");
            }
            else if (context.GenericMotiveDescriptions.Contains(Normalize(description)))
            {
                add("4190", "La descripción del motivo «otros» no puede ser solo una de las palabras genéricas que SUNAT observa (por ejemplo «traslado» o «venta»): diga qué se traslada y por qué.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.MotiveDescription))
        {
            add("3457", "La descripción del motivo solo se usa con el motivo «otros» (13).");
        }
    }

    // ---------- parties ----------

    private static void Parties(CreateGreRequest request, GreValidationContext context, Action<string, string> add)
    {
        var motive = request.MotiveCode ?? string.Empty;
        var recipient = request.Recipient;
        if (recipient is null)
        {
            add("2757", "Falta el destinatario.");
        }
        else
        {
            Party("destinatario", recipient, "2757", add);
            var same = SameParty(recipient, "6", context.SenderRuc);
            if (RecipientIsNotSender.Contains(motive) && same)
            {
                add("2555", "Con este motivo el destinatario no puede ser el propio remitente.");
            }

            if (RecipientIsSender.Contains(motive) && !same)
            {
                add("2554", "Con este motivo el destinatario es el propio remitente.");
            }

            if (motive is "06" or "17" && recipient.DocumentTypeCode != "6")
            {
                add("3417", "Con este motivo el destinatario se identifica con RUC.");
            }
        }

        var supplierMotive = motive is "02" or "07" or "13";
        if (request.Supplier is { } supplier)
        {
            if (!supplierMotive)
            {
                add("4054", "El proveedor solo se informa con los motivos compra (02), recojo de bienes transformados (07) y otros (13).");
            }
            else
            {
                Party("proveedor", supplier, "2723", add);
                if (motive == "02" && supplier.DocumentTypeCode is not ("1" or "4" or "6" or "7"))
                {
                    add("3447", "Con el motivo compra el proveedor se identifica con DNI, carnet de extranjería, RUC o pasaporte.");
                }

                if (motive == "07" && supplier.DocumentTypeCode != "6")
                {
                    add("3447", "Con el motivo recojo de bienes transformados el proveedor se identifica con RUC.");
                }

                if (motive is "02" or "07" && SameParty(supplier, "6", context.SenderRuc))
                {
                    add("3448", "El proveedor no puede ser el propio remitente.");
                }
            }
        }

        if (request.Buyer is { } buyer)
        {
            if (motive is not ("03" or "13"))
            {
                add("4377", "El comprador solo se informa con los motivos venta con entrega a terceros (03) y otros (13).");
            }
            else
            {
                Party("comprador", buyer, "3333", add);
                if (motive == "03" && SameParty(buyer, "6", context.SenderRuc))
                {
                    add("3334", "El comprador no puede ser el propio remitente.");
                }

                if (motive == "03" && recipient is not null && SameParty(buyer, recipient.DocumentTypeCode, recipient.DocumentNumber))
                {
                    add("3335", "El comprador no puede ser el destinatario.");
                }
            }
        }
        else if (motive == "03")
        {
            add("4378", "Con el motivo venta con entrega a terceros hay que informar al comprador.");
        }
    }

    private static bool SameParty(GrePartyInput party, string type, string number) =>
        string.Equals(party.DocumentTypeCode?.Trim(), type, StringComparison.Ordinal) && string.Equals(party.DocumentNumber?.Trim(), number, StringComparison.OrdinalIgnoreCase);

    /// <summary>A party with a catalogue 06 document, a name of up to 250 characters and a number that has the shape of its type.</summary>
    private static void Party(string label, GrePartyInput party, string rule, Action<string, string> add)
    {
        var type = party.DocumentTypeCode?.Trim() ?? string.Empty;
        var number = party.DocumentNumber?.Trim() ?? string.Empty;
        if (number.Length == 0 || type.Length == 0)
        {
            add(rule, $"Falta el tipo o el número de documento del {label}.");
        }
        else if (!DocumentShapeIsValid(type, number))
        {
            add("2758", $"El documento del {label} no tiene la forma de su tipo (RUC de 11 dígitos con dígito verificador, DNI de 8 dígitos, otros alfanuméricos de hasta 15).");
        }

        if (!IsPlainText(party.Name ?? string.Empty, 1, 250))
        {
            add(rule, $"El nombre o razón social del {label} es obligatorio (hasta 250 caracteres, sin saltos de línea).");
        }
    }

    private static bool DocumentShapeIsValid(string type, string number) => type switch
    {
        "6" => Ruc.Create(number).IsSuccess,
        "1" => DniPattern().IsMatch(number),
        "0" or "4" or "7" or "A" => ForeignDocumentPattern().IsMatch(number),
        _ => false,
    };

    // ---------- shipment ----------

    private static void Weight(decimal grossWeight, string? unit, int? packageCount, Action<string, string> add)
    {
        if (grossWeight <= 0 || grossWeight >= 1_000_000_000_000m || decimal.Round(grossWeight, 3) != grossWeight)
        {
            add("2523", "El peso bruto total es positivo, de hasta 12 enteros y 3 decimales.");
        }

        if (unit is not ("KGM" or "TNE"))
        {
            add("2523", "La unidad del peso bruto es KGM (kilogramos) o TNE (toneladas).");
        }

        if (packageCount is < 0)
        {
            add("3489", "El número de bultos es un entero de hasta 13 dígitos.");
        }
    }

    private static void Shipment(CreateGreRequest request, DateOnly issueDate, Action<string, string> add)
    {
        Weight(request.GrossWeight, request.WeightUnit, request.PackageCount, add);

        if (request.ModalityCode == "02")
        {
            if (request.TransferStartDate is not { } start)
            {
                add("3406", "Con transporte privado hay que indicar la fecha de inicio del traslado.");
            }
            else if (start < issueDate)
            {
                add("3343", "La fecha de inicio del traslado no puede ser anterior a la de emisión.");
            }

            if (request.HandoverDate is not null)
            {
                add("3617", "La fecha de entrega al transportista es solo del transporte público.");
            }
        }
        else if (request.ModalityCode == "01")
        {
            if (request.HandoverDate is not { } handover)
            {
                add("3617", "Con transporte público hay que indicar la fecha de entrega de los bienes al transportista.");
            }
            else if (handover < issueDate)
            {
                add("3618", "La fecha de entrega al transportista no puede ser anterior a la de emisión.");
            }

            if (request.TransferStartDate is not null)
            {
                add("3406", "La fecha de inicio del traslado es solo del transporte privado.");
            }
        }
    }

    // ---------- transport ----------

    private static void Transport(CreateGreRequest request, Action<string, string> add)
    {
        var m1 = request.VehicleCategoryM1OrL;
        var hasVehicle = request.Vehicle is not null || request.SecondaryVehicles is { Count: > 0 };
        var hasDriver = request.Driver is not null || request.SecondaryDrivers is { Count: > 0 };

        if (request.ModalityCode == "02")
        {
            if (request.Carrier is not null)
            {
                add("3347", "Con transporte privado no se informa transportista.");
            }

            if (m1)
            {
                if (hasVehicle || hasDriver)
                {
                    add("3452", "En vehículos de categoría M1 o L no se informan vehículo ni conductor.");
                }
            }
            else
            {
                if (request.Vehicle is null)
                {
                    add("2566", "Con transporte privado hay que informar el vehículo (placa).");
                }

                if (request.Driver is null)
                {
                    add("3357", "Con transporte privado hay que informar al conductor principal.");
                }
            }
        }
        else if (request.ModalityCode == "01")
        {
            if (hasVehicle || hasDriver)
            {
                add("3354", "Con transporte público el vehículo y el conductor son del transportista y no se informan aquí.");
            }

            if (!m1)
            {
                Carrier(request.Carrier, add);
            }
        }

        if (request.Vehicle is not null)
        {
            VehicleRules(request.Vehicle, "principal", add);
        }

        var secondaries = request.SecondaryVehicles ?? [];
        if (secondaries.Count > 2)
        {
            add("4389", "Se admiten hasta 2 vehículos secundarios.");
        }

        foreach (var vehicle in secondaries)
        {
            VehicleRules(vehicle, "secundario", add);
        }

        if (request.Driver is not null)
        {
            DriverRules(request.Driver, "principal", add);
        }

        var secondaryDrivers = request.SecondaryDrivers ?? [];
        if (secondaryDrivers.Count > 2)
        {
            add("3362", "Se admiten hasta 2 conductores secundarios.");
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

    private static void Carrier(GreCarrierInput? carrier, Action<string, string> add)
    {
        if (carrier is null)
        {
            add("2558", "Con transporte público hay que informar al transportista (RUC y razón social).");
            return;
        }

        if (!Ruc.Create(carrier.Ruc?.Trim() ?? string.Empty).IsSuccess)
        {
            add("2485", "El RUC del transportista no es válido.");
        }

        if (!IsPlainText(carrier.Name ?? string.Empty, 3, 250))
        {
            add("2563", "La razón social del transportista tiene de 3 a 250 caracteres.");
        }

        if (carrier.MtcRegistration is { Length: > 0 } registration && !MtcRegistrationPattern().IsMatch(registration.Trim().ToUpperInvariant()))
        {
            add("4392", "El registro MTC del transportista tiene hasta 20 letras mayúsculas y dígitos.");
        }
    }

    private static void VehicleRules(GreVehicleInput vehicle, string kind, Action<string, string> add)
    {
        var plate = vehicle.Plate?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!PlatePattern().IsMatch(plate) || plate.All(c => c == '0'))
        {
            add("2567", $"La placa del vehículo {kind} tiene de 6 a 8 letras mayúsculas y dígitos, sin guion ni espacios.");
        }

        if (vehicle.CirculationCard is { Length: > 0 } card && (!CirculationCardPattern().IsMatch(card.Trim().ToUpperInvariant())))
        {
            add("3355", $"La tarjeta de circulación o certificado del vehículo {kind} tiene de 10 a 15 letras mayúsculas y dígitos.");
        }
    }

    private static void DriverRules(GreDriverInput driver, string kind, Action<string, string> add)
    {
        var type = driver.DocumentTypeCode?.Trim() ?? string.Empty;
        var number = driver.DocumentNumber?.Trim() ?? string.Empty;
        if (type is "" or "6" || !DocumentShapeIsValid(type, number))
        {
            add("2571", $"El documento del conductor {kind} es un DNI de 8 dígitos u otro documento alfanumérico de hasta 15 (no RUC).");
        }

        if (!IsPlainText(driver.FirstNames ?? string.Empty, 1, 250) || !IsPlainText(driver.LastNames ?? string.Empty, 1, 250))
        {
            add("3360", $"Los nombres y apellidos del conductor {kind} son obligatorios (hasta 250 caracteres cada uno).");
        }

        var licence = driver.LicenseNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!LicencePattern().IsMatch(licence) || licence.All(c => c == '0'))
        {
            add("2573", $"La licencia del conductor {kind} tiene de 9 a 10 letras mayúsculas y dígitos, no solo ceros.");
        }
    }

    // ---------- points ----------

    private static void Points(CreateGreRequest request, GreValidationContext context, string motive, Action<string, string> add)
    {
        Point("partida", request.Origin, add);
        Point("llegada", request.Destination, add);

        if (motive == "04")
        {
            foreach (var (label, address) in new[] { ("partida", request.Origin), ("llegada", request.Destination) })
            {
                if (address?.EstablishmentCode is null or "")
                {
                    add("3365", $"Con el traslado entre establecimientos hay que informar el código de establecimiento de {label}.");
                }
                else if (address.EstablishmentRuc?.Trim() != context.SenderRuc)
                {
                    add("3414", $"Con el traslado entre establecimientos el RUC del punto de {label} es el del remitente.");
                }
            }
        }

        if (motive is "02" or "07" && request.Origin?.EstablishmentRuc?.Trim() == context.SenderRuc)
        {
            add("3411", "Con este motivo el establecimiento de partida no es del remitente.");
        }

        if (motive is "01" or "03" or "05" or "06" or "14" or "17" && request.Destination?.EstablishmentRuc?.Trim() == context.SenderRuc)
        {
            add("3411", "Con este motivo el establecimiento de llegada no es del remitente.");
        }
    }

    private static void Point(string label, GreAddressInput? address, Action<string, string> add)
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

        if (!IsPlainText(address.Address ?? string.Empty, 3, 500))
        {
            add("2574", $"La dirección del punto de {label} tiene de 3 a 500 caracteres, sin saltos de línea.");
        }

        var ruc = address.EstablishmentRuc?.Trim() ?? string.Empty;
        var code = address.EstablishmentCode?.Trim() ?? string.Empty;
        if (ruc.Length > 0 && !Ruc.Create(ruc).IsSuccess)
        {
            add("3409", $"El RUC del establecimiento de {label} no es válido.");
        }

        if (ruc.Length > 0 && code.Length == 0)
        {
            add("3365", $"Falta el código del establecimiento de {label}.");
        }

        if (code.Length > 0 && ruc.Length == 0)
        {
            add("3410", $"Falta el RUC del establecimiento de {label}.");
        }

        if (code.Length > 0 && !EstablishmentPattern().IsMatch(code))
        {
            add("3365", $"El código del establecimiento de {label} tiene 4 dígitos.");
        }
    }

    // ---------- goods ----------

    private static void Goods(IReadOnlyList<GreGoodInput>? list, GreValidationContext context, Action<string, string> add, bool required = true)
    {
        var goods = list ?? [];
        if (goods.Count == 0 && required)
        {
            add("2580", "Hay que informar al menos un bien a trasladar.");
        }

        if (goods.Count > 9999)
        {
            add("2023", "El número de orden del bien tiene hasta 4 dígitos: no más de 9999 bienes.");
        }

        for (var i = 0; i < goods.Count; i++)
        {
            var good = goods[i];
            var at = $"Bien {i + 1}";
            if (!IsPlainText(good.Description ?? string.Empty, 3, 500))
            {
                add("2781", $"{at}: la descripción tiene de 3 a 500 caracteres.");
            }

            var unit = good.UnitCode?.Trim() ?? string.Empty;
            if (context.Catalogs.UnitCodes.Count == 0)
            {
                // The catalogue 03 is the UN/ECE Recommendation 20, an external list that is not loaded: only the shape of the code is checked and SUNAT observes an unknown one (4320).
                if (!UnitShape().IsMatch(unit))
                {
                    add("2883", $"{at}: la unidad de medida es obligatoria y tiene hasta 3 letras o dígitos.");
                }
            }
            else if (!context.Catalogs.UnitCodes.Contains(unit))
            {
                add("4320", $"{at}: la unidad de medida no está en el catálogo 03.");
            }

            if (good.Quantity <= 0 || good.Quantity >= 1_000_000_000_000m || decimal.Round(good.Quantity, 10) != good.Quantity)
            {
                add("2780", $"{at}: la cantidad es positiva, de hasta 12 enteros y 10 decimales.");
            }

            if (good.Code is { Length: > 0 } && !IsPlainText(good.Code, 1, 30))
            {
                add("4085", $"{at}: el código del bien tiene hasta 30 caracteres.");
            }

            if (good.SunatProductCode is { Length: > 0 } sunat && (!SunatProductPattern().IsMatch(sunat) || sunat.All(c => c == '0')))
            {
                add("3002", $"{at}: el código de producto SUNAT es numérico de hasta 8 dígitos, no solo ceros.");
            }

            if (good.Gtin is { Length: > 0 } gtin && !GtinPattern().IsMatch(gtin))
            {
                add("3375", $"{at}: el código GTIN es alfanumérico de hasta 14 caracteres.");
            }
        }
    }

    // ---------- related documents ----------

    private static void RelatedDocuments(CreateGreRequest request, GreValidationContext context, GrePartyInput? recipient, Action<string, string> add)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in request.RelatedDocuments ?? [])
        {
            var type = document.TypeCode?.Trim() ?? string.Empty;
            var number = document.Number?.Trim() ?? string.Empty;
            if (!context.Catalogs.RelatedDocumentApplicability.TryGetValue(type, out var applicability) || !applicability.Contains("remitente", StringComparison.Ordinal))
            {
                add("2692", $"El documento relacionado «{type}» no está en el catálogo 61 para la guía del remitente.");
                continue;
            }

            if (CustomsDocuments.Contains(type))
            {
                add("3445", $"El documento relacionado «{type}» (aduanas, manifiesto o terminal portuario) es de los motivos que aún no se emiten aquí.");
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

            var issuer = document.IssuerRuc?.Trim() ?? string.Empty;
            if (RelatedDocumentsWithIssuer.Contains(type) && issuer.Length == 0)
            {
                add("3380", $"Falta el RUC del emisor del documento relacionado «{type}».");
            }

            if (issuer.Length > 0 && !Ruc.Create(issuer).IsSuccess)
            {
                add("3409", $"El RUC del emisor del documento relacionado «{type}» no es válido.");
            }

            IssuerMatches(request.MotiveCode, type, issuer, context.SenderRuc, recipient, request.Supplier, add);
        }
    }

    private static void IssuerMatches(string? motive, string type, string issuer, string senderRuc, GrePartyInput? recipient, GrePartyInput? supplier, Action<string, string> add)
    {
        if (issuer.Length == 0)
        {
            return;
        }

        if (motive is "01" or "03" && type is "01" or "03" or "12" && issuer != senderRuc)
        {
            add("3381", $"Con este motivo el emisor del documento relacionado «{type}» es el propio remitente.");
        }

        if (type == "09" && issuer != senderRuc)
        {
            add("3381", "El emisor de la guía relacionada «09» es el propio remitente.");
        }

        if (motive == "02" && type is "04" or "48" && issuer != senderRuc)
        {
            add("3381", $"Con el motivo compra el emisor del documento relacionado «{type}» es el propio remitente.");
        }

        if (motive == "06" && type is "01" or "03" or "12" && recipient is { DocumentTypeCode: "6" } && issuer != recipient.DocumentNumber.Trim())
        {
            add("3381", $"Con el motivo devolución el emisor del documento relacionado «{type}» es el destinatario.");
        }

        if (motive is "02" or "07" && type is "01" or "03" or "12" && supplier is { DocumentTypeCode: "6" } && issuer != supplier.DocumentNumber.Trim())
        {
            add("3442", $"El emisor del documento relacionado «{type}» es el proveedor.");
        }
    }

    /// <summary>The shapes of rule 3441 for the documents that the first delivery accepts; any other type is alphanumeric of up to 100 characters.</summary>
    private static bool RelatedNumberIsValid(string type, string number) => type switch
    {
        "01" => Regex.IsMatch(number, "^(F[A-Z0-9]{3}|E001|[0-9]{1,4})-[0-9]{1,8}$", RegexOptions.CultureInvariant) && PositiveNumber(number),
        "03" => Regex.IsMatch(number, "^(B[A-Z0-9]{3}|EB01|[0-9]{1,4})-[0-9]{1,8}$", RegexOptions.CultureInvariant) && PositiveNumber(number),
        "04" => Regex.IsMatch(number, "^(L[A-Z0-9]{3}|E001|[0-9]{1,4})-[0-9]{1,8}$", RegexOptions.CultureInvariant) && PositiveNumber(number),
        "09" => Regex.IsMatch(number, "^(T[A-Z0-9]{3}|EG07|EG02)-[0-9]{1,8}$", RegexOptions.CultureInvariant) && PositiveNumber(number),
        "12" => Regex.IsMatch(number, "^[a-zA-Z0-9-]{1,20}-[a-zA-Z0-9-]{1,20}$", RegexOptions.CultureInvariant),
        "48" => Regex.IsMatch(number, "^[0-9]{1,4}-[0-9]{1,7}$", RegexOptions.CultureInvariant) && PositiveNumber(number),
        "49" or "80" => Regex.IsMatch(number, "^[0-9]{1,15}$", RegexOptions.CultureInvariant) && number.Any(c => c != '0'),
        "81" => IsPlainText(number, 1, 20),
        _ => IsPlainText(number, 1, 100),
    };

    /// <summary>The number after the last dash is greater than zero.</summary>
    private static bool PositiveNumber(string number) =>
        long.TryParse(number[(number.LastIndexOf('-') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;

    // ---------- text ----------

    /// <summary>
    /// Text of the shape that the rules ask: between the lengths, any character, space included, but no other whitespace (line break, tab…), and not only spaces.
    /// </summary>
    public static bool IsPlainText(string value, int min, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Trim().Length < min || value.Length > max)
        {
            return false;
        }

        return value.All(c => !char.IsControl(c) && (!char.IsWhiteSpace(c) || c == ' '));
    }

    /// <summary>Lower case, no accents, single spaces: how the generic descriptions of the motive «otros» are compared.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var plain = new string(decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(plain, @"\s+", " ", RegexOptions.CultureInvariant).Trim().TrimEnd('.');
    }
}
