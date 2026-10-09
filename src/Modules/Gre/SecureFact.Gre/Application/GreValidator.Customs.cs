using System.Globalization;
using System.Text.RegularExpressions;
using SecureFact.Gre.Contracts;

namespace SecureFact.Gre.Application;

/// <summary>
/// The rules of the sheet «Guía-Remitente2_0» of S27 for the guides that carry customs documents: import (08), export (09) and foreign goods (19), and for the itinerant issuer (18) (R-074 to R-077,
/// ADR-059). What SUNAT answers from its registers (that the declaration, the manifest or the delivery order exist, who the importer or exporter is, the lines of the manifest and their containers)
/// is left to the CDR. A rule that is ours and not SUNAT's carries «SF» instead of a code.
/// </summary>
internal static partial class GreValidator
{
    [GeneratedRegex("^[A-Z0-9/-]{1,17}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContainerPattern();

    [GeneratedRegex("^[A-Z0-9]+(,[A-Z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SealPattern();

    [GeneratedRegex("^[A-Z0-9/\\\\-]{1,25}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TransportDocumentPattern();

    [GeneratedRegex("^[1-9][0-9]{0,4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TransportDetailPattern();

    [GeneratedRegex("^[0-9]{1,4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeclarationSeriesPattern();

    [GeneratedRegex("^[0-9]{3}-[0-9]{4}-[0-9]{2}-[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeclarationNumberPattern();

    [GeneratedRegex("^[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ManifestQuantityPattern();

    /// <summary>What the related documents and the customs data say about the guide, worked out once.</summary>
    internal sealed record CustomsView(bool IsCustoms, bool HasDeclaration, bool Has91, bool Has92, bool WholeTransfer, bool ManifestContainers, IReadOnlyList<GreContainerInput> Containers, IReadOnlyList<string> DeclarationNumbers)
    {
        public static CustomsView Of(CreateGreRequest request)
        {
            var motive = request.MotiveCode ?? string.Empty;
            var types = (request.RelatedDocuments ?? []).Select(d => d.TypeCode?.Trim() ?? string.Empty).ToList();
            var customs = request.Customs;
            return new CustomsView(
                motive is "08" or "09" or "19",
                types.Any(t => t is "50" or "52"),
                types.Contains("91"),
                types.Contains("92"),
                customs?.WholeTransfer == true,
                customs?.ManifestContainers == true,
                (customs?.Containers ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Number)).ToList(),
                (request.RelatedDocuments ?? []).Where(d => d.TypeCode?.Trim() is "50" or "52").Select(d => d.Number?.Trim() ?? string.Empty).ToList());
        }
    }

    // ---------- related documents ----------

    private static bool CustomsDocumentAllowed(string? motive, string type) => type switch
    {
        "50" or "52" => motive is "08" or "09" or "19",
        _ => motive == "19",
    };

    /// <summary>With import and export only the declarations and a previous guide of the sender relate (3445); with foreign goods only the customs documents.</summary>
    private static bool CustomsOtherDocumentFits(string? motive, string type) => motive switch
    {
        "08" or "09" => type is "09" or "50" or "52",
        "19" => type is "50" or "52" or "91" or "92",
        _ => true,
    };

    private static void CustomsDocumentSet(string motive, CustomsView customs, Action<string, string> add)
    {
        if (!customs.IsCustoms)
        {
            return;
        }

        if (motive is "08" or "09" && !customs.HasDeclaration)
        {
            add("3440", "Con importación (08) o exportación (09) hay que relacionar la declaración aduanera (DAM, 50) o la simplificada (DS, 52).");
        }

        if (motive == "19")
        {
            if (!customs.HasDeclaration && !customs.Has91 && !customs.Has92)
            {
                add("3493", "Con el traslado de mercancía extranjera (19) hay que relacionar la declaración (50 o 52), el manifiesto de carga (91) o la orden de entrega del terminal portuario (92).");
            }

            if (customs.Has91 && customs.HasDeclaration)
            {
                add("3463", "El manifiesto de carga (91) no se relaciona junto con una declaración (50 o 52).");
            }

            if (customs.Has92 && (customs.HasDeclaration || customs.Has91))
            {
                add("3613", "La orden de entrega del terminal portuario (92) no se relaciona junto con otros documentos de aduanas.");
            }
        }
    }

    private static bool CustomsNumberIsValid(string type, string? motive, string number, string? portType) => type switch
    {
        "50" => Regex.IsMatch(number, motive switch
        {
            "08" => "^[0-9]{3}-[0-9]{4}-10-[1-9][0-9]{0,5}$",
            "09" => "^[0-9]{3}-[0-9]{4}-40-[1-9][0-9]{0,5}$",
            _ => "^[0-9]{3}-[0-9]{4}-(10|20|21|30|36|70|80)-[1-9][0-9]{0,5}$",
        }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        "52" => Regex.IsMatch(number, motive == "09" ? "^[0-9]{3}-[0-9]{4}-48-[1-9][0-9]{0,5}$" : "^[0-9]{3}-[0-9]{4}-18-[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        "91" => portType is "1" or "2" && Regex.IsMatch(number, $"^01-[0-9]{{3}}-{(portType == "1" ? "1" : "4")}-[0-9]{{4}}-[1-9][0-9]{{0,5}}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        _ => Regex.IsMatch(number, "^[0-9]{1,50}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
    };

    private static string CustomsNumberHint(string type, string? motive) => type switch
    {
        "50" => motive switch { "08" => " (aduana-año-10-correlativo)", "09" => " (aduana-año-40-correlativo)", _ => " (aduana-año-régimen 10, 20, 21, 30, 36, 70 u 80-correlativo)" },
        "52" => motive == "09" ? " (aduana-año-48-correlativo)" : " (aduana-año-18-correlativo)",
        "91" => " (01-aduana-vía 1 si es puerto o 4 si es aeropuerto-año-correlativo; hay que indicar antes el puerto o el aeropuerto)",
        "92" => " (hasta 50 dígitos)",
        _ => string.Empty,
    };

    // ---------- points ----------

    private static void CustomsPoints(CreateGreRequest request, GreValidationContext context, string motive, CustomsView customs, Action<string, string> add)
    {
        if (!customs.IsCustoms)
        {
            return;
        }

        var portCode = request.Customs?.PortCode?.Trim() ?? string.Empty;
        var portUbigeo = PortUbigeo(context, request.Customs);
        if (motive is "08" or "19" && portUbigeo is not null && request.Origin?.UbigeoCode?.Trim() != portUbigeo)
        {
            add("3364", "El ubigeo del punto de partida es el del puerto o aeropuerto informado.");
        }

        if (motive == "09" && request.Customs?.PortType == "1" && portUbigeo is not null && request.Destination?.UbigeoCode?.Trim() != portUbigeo)
        {
            add("3364", "El ubigeo del punto de llegada es el del puerto informado.");
        }

        if (motive == "08" && portCode.Length == 0 && string.IsNullOrWhiteSpace(request.Origin?.EstablishmentCode))
        {
            add("3365", "Con importación (08) sin puerto ni aeropuerto hay que informar el código de establecimiento del punto de partida (el depósito temporal).");
        }

        if (motive == "09" && portCode.Length == 0 && string.IsNullOrWhiteSpace(request.Destination?.EstablishmentCode))
        {
            add("3369", "Con exportación (09) sin puerto hay que informar el código de establecimiento del punto de llegada.");
        }

        if (motive == "19")
        {
            if (string.IsNullOrWhiteSpace(request.Destination?.EstablishmentCode))
            {
                add("3369", "Con el traslado de mercancía extranjera (19) hay que informar el código de establecimiento del punto de llegada.");
            }
            else if (request.Destination.EstablishmentRuc?.Trim() != request.Recipient?.DocumentNumber?.Trim())
            {
                add("3488", "El RUC del establecimiento de llegada es el del destinatario.");
            }
        }
    }

    private static string? PortUbigeo(GreValidationContext context, GreCustomsInput? customs)
    {
        var code = customs?.PortCode?.Trim() ?? string.Empty;
        var map = customs?.PortType switch { "1" => context.Catalogs.Ports, "2" => context.Catalogs.Airports, _ => null };
        return map is not null && map.TryGetValue(code, out var ubigeo) ? ubigeo : null;
    }

    // ---------- shipment: port, weights, containers ----------

    private static void CustomsShipment(CreateGreRequest request, GreValidationContext context, CustomsView customs, Action<string, string> add)
    {
        var motive = request.MotiveCode ?? string.Empty;
        var data = request.Customs;
        if (!customs.IsCustoms)
        {
            if (data is { WholeTransfer: true })
            {
                add("3392", "El traslado total de la declaración solo existe con importación (08), exportación (09) y mercancía extranjera (19).");
            }

            if (data is { ManifestContainers: true })
            {
                add("3478", "El traslado en contenedores del manifiesto solo existe con el motivo 19 y el manifiesto de carga (91).");
            }

            if (data?.NetWeight is not null)
            {
                add("3395", "El peso neto solo existe con importación (08), exportación (09) y mercancía extranjera (19).");
            }

            if (!string.IsNullOrWhiteSpace(data?.WeightNote))
            {
                add("3418", "El sustento de la diferencia de peso solo existe con importación (08), exportación (09) y mercancía extranjera (19).");
            }

            if (!string.IsNullOrWhiteSpace(data?.PortCode) || (data?.Containers?.Count ?? 0) > 0)
            {
                add("SF", "El puerto, el aeropuerto y los contenedores solo se informan con importación (08), exportación (09) y mercancía extranjera (19).");
            }

            return;
        }

        if (customs.WholeTransfer && (customs.Has91 || customs.Has92))
        {
            add("3485", "El traslado total de la declaración no existe con el manifiesto de carga (91) ni con la orden de entrega (92).");
        }

        if (customs.ManifestContainers && !(motive == "19" && customs.Has91))
        {
            add("3478", "El traslado en contenedores del manifiesto solo existe con el motivo 19 y el manifiesto de carga (91).");
        }

        Port(request, context, motive, add);
        NetWeight(request, customs, motive, add);
        Containers(request, customs, motive, add);
    }

    private static void Port(CreateGreRequest request, GreValidationContext context, string motive, Action<string, string> add)
    {
        var data = request.Customs;
        var code = data?.PortCode?.Trim() ?? string.Empty;
        var type = data?.PortType?.Trim() ?? string.Empty;
        if (code.Length == 0)
        {
            if (type.Length > 0)
            {
                add("4413", "Falta el código del puerto o del aeropuerto.");
            }

            if (motive == "19")
            {
                add(type == "2" ? "3484" : "3483", "Con el traslado de mercancía extranjera (19) hay que informar el puerto o el aeropuerto.");
            }

            return;
        }

        if (type is not ("1" or "2"))
        {
            add("4416", "El tipo de locación es 1 (puerto) o 2 (aeropuerto).");
            return;
        }

        var catalogue = type == "1" ? context.Catalogs.Ports : context.Catalogs.Airports;
        if (catalogue is not null && !catalogue.ContainsKey(code))
        {
            add(type == "1" ? "3459" : "3460", $"El código «{code}» no está en el catálogo {(type == "1" ? "63 de puertos" : "64 de aeropuertos")}.");
        }

        if (!IsPlainText(data?.PortName ?? string.Empty, 1, 200))
        {
            add("4418", "Falta el nombre del puerto o del aeropuerto (hasta 200 caracteres).");
        }
    }

    private static void NetWeight(CreateGreRequest request, CustomsView customs, string motive, Action<string, string> add)
    {
        var data = request.Customs;
        var net = data?.NetWeight;
        var note = data?.WeightNote;
        if (customs.Has92)
        {
            if (net is not null)
            {
                add("3623", "Con la orden de entrega del terminal portuario (92) no hay peso neto.");
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                add("3624", "Con la orden de entrega del terminal portuario (92) no hay sustento de la diferencia de peso.");
            }

            return;
        }

        if (net is { } value && (value <= 0 || value >= 1_000_000_000_000m || decimal.Round(value, 3) != value))
        {
            add("3397", "El peso neto (en KGM) es positivo, de hasta 12 enteros y 3 decimales.");
        }

        if (motive == "09" && !customs.WholeTransfer)
        {
            if (net is null)
            {
                add("4383", "Con exportación (09), sin traslado total de la declaración, hay que informar el peso neto de los bienes.");
            }

            if (string.IsNullOrWhiteSpace(note))
            {
                add("4387", "Con exportación (09), sin traslado total de la declaración, hay que sustentar la diferencia entre el peso bruto y el peso de los bienes.");
            }
        }

        if (!string.IsNullOrWhiteSpace(note) && !IsPlainText(note, 1, 250))
        {
            add("4428", "El sustento de la diferencia de peso tiene hasta 250 caracteres, sin saltos de línea.");
        }
    }

    private static void Containers(CreateGreRequest request, CustomsView customs, string motive, Action<string, string> add)
    {
        if (customs.Has92)
        {
            if (request.PackageCount is not null)
            {
                add("3626", "Con la orden de entrega del terminal portuario (92) no hay número de bultos.");
            }

            if (customs.Containers.Count > 0)
            {
                add("3627", "Con la orden de entrega del terminal portuario (92) no hay contenedores.");
            }

            return;
        }

        var containers = customs.Containers;
        if (containers.Count > 2)
        {
            add("3420", "Se admiten hasta 2 contenedores.");
        }

        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var seals = new HashSet<string>(StringComparer.Ordinal);
        var sealRequired = !customs.ManifestContainers && (customs.WholeTransfer || motive == "09");
        foreach (var (container, index) in containers.Select((c, i) => (c, i)))
        {
            var at = $"Contenedor {index + 1}";
            var number = container.Number.Trim();
            if (!ContainerPattern().IsMatch(number))
            {
                add("4071", $"{at}: el número tiene hasta 17 letras mayúsculas, dígitos, «-» y «/».");
            }

            if (!numbers.Add(number))
            {
                add("3421", $"{at}: el número de contenedor se repite.");
            }

            var seal = container.Seal?.Trim() ?? string.Empty;
            if (seal.Length == 0)
            {
                if (sealRequired)
                {
                    add("3422", $"{at}: falta el número de precinto.");
                }
            }
            else
            {
                if (!SealPattern().IsMatch(seal) || seal.All(c => c is '0' or ','))
                {
                    add("4074", $"{at}: el precinto tiene hasta 100 letras mayúsculas y dígitos (varios se separan con coma), no solo ceros.");
                }

                if (seal.Length > 100)
                {
                    add("4074", $"{at}: el precinto tiene hasta 100 caracteres.");
                }

                if (!seals.Add(seal))
                {
                    add("3423", $"{at}: el número de precinto se repite.");
                }
            }
        }

        var needsPackaging = customs.HasDeclaration || (motive == "19" && customs.Has91);
        if (!needsPackaging)
        {
            return;
        }

        if (customs.ManifestContainers && containers.Count == 0)
        {
            add("3487", "Con el traslado en contenedores del manifiesto hay que informar el contenedor.");
        }

        if (containers.Count > 0)
        {
            if (request.PackageCount is not null)
            {
                add("3621", "Con contenedores no se informa el número de bultos.");
            }
        }
        else if (request.PackageCount is null)
        {
            add("3419", "Sin contenedores hay que informar el número de bultos o pallets.");
        }
    }

    // ---------- goods ----------

    private static void CustomsGoods(CreateGreRequest request, GreValidationContext context, CustomsView customs, Action<string, string> add)
    {
        var motive = request.MotiveCode ?? string.Empty;
        var goods = request.Goods ?? [];
        if (customs.Has92)
        {
            if (goods.Count > 0)
            {
                add("3629", "Con la orden de entrega del terminal portuario (92) no se informan bienes: la guía lleva una sola línea de forma.");
            }

            return;
        }

        if (customs.Has91)
        {
            ManifestGoods(goods, customs, add);
            return;
        }

        if (goods.Count == 0)
        {
            if (motive == "09" && !customs.WholeTransfer)
            {
                add("2580", "Con exportación (09), sin traslado total de la declaración, hay que informar los bienes.");
            }

            return;
        }

        var units = context.Catalogs.CustomsUnits;
        Goods(goods, context, add, required: false, units: units ?? new HashSet<string>(["U"], StringComparer.Ordinal));
        for (var i = 0; i < goods.Count; i++)
        {
            var at = $"Bien {i + 1}";
            var props = goods[i].Customs;
            var declaration = props?.DeclarationNumber?.Trim() ?? string.Empty;
            var series = props?.DeclarationSeries?.Trim() ?? string.Empty;
            if (motive == "09" && !customs.WholeTransfer)
            {
                if (declaration.Length == 0)
                {
                    add("3427", $"{at}: falta la numeración de la declaración (DAM o DS) del bien.");
                }

                if (series.Length == 0)
                {
                    add("3428", $"{at}: falta el número de serie del bien en la declaración.");
                }
            }

            if (declaration.Length > 0)
            {
                if (!DeclarationNumberPattern().IsMatch(declaration))
                {
                    add("2769", $"{at}: la numeración de la declaración es aduana-año-régimen-correlativo (sin cero inicial en el correlativo).");
                }
                else if (!customs.DeclarationNumbers.Contains(declaration, StringComparer.Ordinal))
                {
                    add("3430", $"{at}: la numeración de la declaración no coincide con ninguna declaración relacionada.");
                }
            }

            if (series.Length > 0 && !DeclarationSeriesPattern().IsMatch(series))
            {
                add("3431", $"{at}: el número de serie del bien en la declaración tiene hasta 4 dígitos.");
            }
        }
    }

    /// <summary>The goods of a cargo manifest (91): each one is a line of a transport document, and with containers each is a line of a container.</summary>
    private static void ManifestGoods(IReadOnlyList<GreGoodInput> goods, CustomsView customs, Action<string, string> add)
    {
        if (goods.Count == 0)
        {
            add("2580", "Con el manifiesto de carga (91) hay que informar los bienes (las líneas del manifiesto).");
            return;
        }

        var lines = new HashSet<string>(StringComparer.Ordinal);
        var containerNumbers = customs.Containers.Select(c => c.Number.Trim()).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < goods.Count; i++)
        {
            var good = goods[i];
            var at = $"Bien {i + 1}";
            var props = good.Customs;
            if (!IsPlainText(good.Description ?? string.Empty, 3, 500))
            {
                add("2781", $"{at}: la descripción tiene de 3 a 500 caracteres.");
            }

            var document = props?.TransportDocument?.Trim() ?? string.Empty;
            var detail = props?.TransportDetail?.Trim() ?? string.Empty;
            if (document.Length == 0)
            {
                add("3490", $"{at}: falta el documento de transporte del manifiesto.");
            }
            else if (!TransportDocumentPattern().IsMatch(document))
            {
                add("3466", $"{at}: el documento de transporte tiene hasta 25 letras mayúsculas, dígitos, «/», «\\» y «-».");
            }

            if (detail.Length == 0)
            {
                add("3491", $"{at}: falta el número de detalle del documento de transporte.");
            }
            else if (!TransportDetailPattern().IsMatch(detail))
            {
                add("3468", $"{at}: el número de detalle tiene hasta 5 dígitos, mayor que cero y sin cero inicial.");
            }

            var container = props?.ManifestContainer?.Trim() ?? string.Empty;
            if (customs.ManifestContainers)
            {
                if (container.Length == 0)
                {
                    add("3480", $"{at}: falta el contenedor de la línea del manifiesto.");
                }
                else if (!ContainerPattern().IsMatch(container))
                {
                    add("3470", $"{at}: el contenedor tiene hasta 17 letras mayúsculas, dígitos, «-» y «/».");
                }
                else if (!containerNumbers.Contains(container))
                {
                    add("3486", $"{at}: el contenedor no es uno de los contenedores de la guía.");
                }

                if (props?.EmptyContainer is null)
                {
                    add("3482", $"{at}: falta el indicador de contenedor vacío.");
                }
                else if (props.EmptyContainer == false && string.IsNullOrWhiteSpace(props.Seal))
                {
                    add("3481", $"{at}: un contenedor que no está vacío lleva precinto.");
                }
            }
            else
            {
                if (good.UnitCode?.Trim() != "U")
                {
                    add("3446", $"{at}: con el manifiesto de carga la unidad de medida es «U».");
                }

                if (!ManifestQuantityPattern().IsMatch(good.Quantity.ToString(CultureInfo.InvariantCulture)))
                {
                    add("2780", $"{at}: la cantidad es un entero positivo de hasta 10 dígitos.");
                }
            }

            if (document.Length > 0 && detail.Length > 0 && !lines.Add($"{detail}|{document}|{container}"))
            {
                add(customs.ManifestContainers ? "3492" : "3472", $"{at}: la línea del manifiesto (documento de transporte, detalle{(customs.ManifestContainers ? " y contenedor" : string.Empty)}) se repite.");
            }
        }
    }
}
