using System.Globalization;
using SecureFact.Gre.Contracts;
using SecureFact.Platform.Printing;

namespace SecureFact.Gre.Printing;

/// <summary>Everything the printed representation of a guide is made of, already read: the issuer, the numbering, the answer of SUNAT and the request as it was issued.</summary>
/// <param name="IssueTime">The time of the XML, as «hh:mm:ss».</param>
/// <param name="QrUrl">The address that SUNAT gave in the CDR, when it gave one (ADR-058); null when there is none.</param>
/// <param name="Names">Catalogue descriptions by catalogue number and code (18 modality, 20 motive, 61 related document, 6 identity document).</param>
internal sealed record GrePrintModel(
    string IssuerRuc,
    string IssuerName,
    string IssuerAddress,
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    string IssueTime,
    GreState State,
    int? CdrResponseCode,
    string? CdrDescription,
    IReadOnlyList<GreObservation> Observations,
    DateTimeOffset? ProcessedAt,
    string DigestValue,
    string? QrUrl,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Names,
    CreateGreRequest? SenderRequest,
    CreateGreCarrierRequest? CarrierRequest);

/// <summary>
/// The printed representation of a guide of the sender (09) or of the carrier (31), as a PDF. It shows the data that RS 123-2022/SUNAT (art. 18 of RS 188-2010 as it amends it) puts in the guide
/// (issuer, RUC, denomination, date, time, numbering and, for the carrier, the registration of the MTC) and the data of the transfer that the guide states. The QR is the one that SUNAT gives
/// in the CDR; without it the guide is identified by the RUC, the series and the number, which is what the inspector accepts as well (art. 6 of RS 255-2015/SUNAT as amended). A guide that SUNAT
/// has not accepted carries a mark: it does not support the transfer (R-010, art. 34).
/// </summary>
internal static class GrePdfRenderer
{
    private const double Margin = 36;
    private const double Left = Margin;
    private const double Right = PdfWriter.PageWidth - Margin;
    private const double ContentWidth = Right - Left;
    private const double Bottom = 48;
    private const double LabelWidth = 118;
    private const double QrSize = 104;
    private const double QuietZone = 3;

    public static byte[] Render(GrePrintModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var isCarrier = model.DocumentTypeCode == "31";
        var denomination = isCarrier ? "GUÍA DE REMISIÓN ELECTRÓNICA - TRANSPORTISTA" : "GUÍA DE REMISIÓN ELECTRÓNICA - REMITENTE";
        var number = $"{model.Series}-{model.Number.ToString(CultureInfo.InvariantCulture)}";
        var flow = new Flow(new PdfWriter(), model, denomination, number);
        flow.NewPage(first: true);

        flow.Pair("Fecha de emisión", model.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        flow.Pair("Hora de emisión", model.IssueTime);
        if (model.SenderRequest is { } sender)
        {
            Sender(flow, model, sender);
        }
        else if (model.CarrierRequest is { } carrier)
        {
            Carrier(flow, model, carrier);
        }

        Footer(flow, model, number);
        return flow.Finish($"{denomination} {number}");
    }

    // ---------- guide of the sender ----------

    private static void Sender(Flow flow, GrePrintModel model, CreateGreRequest request)
    {
        flow.Heading("Datos del traslado");
        flow.Pair("Motivo del traslado", $"{request.MotiveCode} - {Name(model, "20", request.MotiveCode)}");
        if (!string.IsNullOrWhiteSpace(request.MotiveDescription))
        {
            flow.Pair("Descripción del motivo", request.MotiveDescription);
        }

        flow.Pair("Modalidad de transporte", $"{request.ModalityCode} - {Name(model, "18", request.ModalityCode)}");
        if (request.TransferStartDate is { } start)
        {
            flow.Pair("Inicio del traslado", Date(start));
        }

        if (request.HandoverDate is { } handover)
        {
            flow.Pair("Entrega al transportista", Date(handover));
        }

        flow.Pair("Peso bruto total", $"{Number(request.GrossWeight)} {request.WeightUnit}");
        if (request.PackageCount is { } packages)
        {
            flow.Pair("Número de bultos", packages.ToString(CultureInfo.InvariantCulture));
        }

        Note(flow, request.Note);

        flow.Heading("Destinatario");
        Party(flow, model, request.Recipient);
        if (request.Supplier is { } supplier)
        {
            flow.Heading("Proveedor");
            Party(flow, model, supplier);
        }

        if (request.Buyer is { } buyer)
        {
            flow.Heading("Comprador");
            Party(flow, model, buyer);
        }

        Points(flow, request.Origin, request.Destination);
        Customs(flow, model, request);

        flow.Heading("Transporte");
        if (request.ModalityCode == "01" && request.Carrier is { } carrier)
        {
            flow.Pair("Transportista", $"RUC {carrier.Ruc} - {carrier.Name}");
            if (!string.IsNullOrWhiteSpace(carrier.MtcRegistration))
            {
                flow.Pair("Registro MTC", carrier.MtcRegistration);
            }
        }
        else if (request.ModalityCode == "02")
        {
            Vehicles(flow, request.Vehicle, request.SecondaryVehicles);
            Drivers(flow, model, request.Driver, request.SecondaryDrivers);
        }

        var indicators = new List<string>();
        if (request.PlannedTransshipment)
        {
            indicators.Add("Transbordo programado");
        }

        if (request.VehicleCategoryM1OrL)
        {
            indicators.Add("Vehículo de categoría M1 o L");
        }

        if (request.ReturnWithEmptyPackaging)
        {
            indicators.Add("Retorno del vehículo con envases o embalajes vacíos");
        }

        if (request.ReturnEmptyVehicle)
        {
            indicators.Add("Retorno del vehículo vacío");
        }

        if (indicators.Count > 0)
        {
            flow.Pair("Indicadores", string.Join("; ", indicators));
        }

        Goods(flow, request.Goods);
        Related(flow, model, request.RelatedDocuments);
    }

    // ---------- guide of the carrier ----------

    private static void Carrier(Flow flow, GrePrintModel model, CreateGreCarrierRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MtcRegistration))
        {
            flow.Pair("Registro MTC", request.MtcRegistration);
        }

        flow.Heading("Datos del traslado");
        flow.Pair("Inicio del traslado", Date(request.TransferStartDate));
        flow.Pair("Peso bruto total", $"{Number(request.GrossWeight)} {request.WeightUnit}");
        if (request.PackageCount is { } packages)
        {
            flow.Pair("Número de bultos", packages.ToString(CultureInfo.InvariantCulture));
        }

        Note(flow, request.Note);

        flow.Heading("Remitente");
        Party(flow, model, request.Sender);
        flow.Heading("Destinatario");
        Party(flow, model, request.Recipient);
        Points(flow, request.Origin, request.Destination);

        flow.Heading("Transporte");
        Vehicles(flow, request.Vehicle, request.SecondaryVehicles);
        Drivers(flow, model, request.Driver, request.SecondaryDrivers);

        if (request.Subcontracted && request.Subcontractor is { } subcontractor)
        {
            flow.Pair("Subcontratador", $"RUC {subcontractor.DocumentNumber} - {subcontractor.Name}");
        }

        flow.Pair("Paga el flete", request.FreightPayer switch
        {
            GreFreightPayer.Subcontractor => "El subcontratador",
            GreFreightPayer.ThirdParty when request.ThirdPartyPayer is { } payer => $"Un tercero: {payer.DocumentNumber} - {payer.Name}",
            GreFreightPayer.ThirdParty => "Un tercero",
            _ => "El remitente",
        });

        var indicators = new List<string>();
        if (request.PlannedTransshipment)
        {
            indicators.Add("Transbordo programado");
        }

        if (request.ReturnWithEmptyPackaging)
        {
            indicators.Add("Retorno del vehículo con envases o embalajes vacíos");
        }

        if (request.ReturnEmptyVehicle)
        {
            indicators.Add("Retorno del vehículo vacío");
        }

        if (indicators.Count > 0)
        {
            flow.Pair("Indicadores", string.Join("; ", indicators));
        }

        if (request.Goods is { Count: > 0 })
        {
            Goods(flow, request.Goods);
        }
        else
        {
            flow.Heading("Bienes");
            var senderGuide = request.RelatedDocuments?.FirstOrDefault(d => d.TypeCode == "09");
            var voucher = request.RelatedDocuments is { Count: > 0 } documents ? documents[0] : null;
            flow.Pair(
                "Bienes",
                senderGuide is not null ? $"Según la guía de remisión remitente {senderGuide.Number}"
                : request.WholeTransfer && voucher is not null ? $"Traslado total de los bienes del documento relacionado {voucher.Number}"
                : "Según el documento relacionado");
        }

        if (request.WholeTransfer && !string.IsNullOrWhiteSpace(request.WholeTransferNote))
        {
            flow.Pair("Anotación sobre los bienes", request.WholeTransferNote);
        }

        Related(flow, model, request.RelatedDocuments);
    }

    // ---------- shared blocks ----------

    private static void Note(Flow flow, string? note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            flow.Pair("Observaciones", note);
        }
    }

    private static void Party(Flow flow, GrePrintModel model, GrePartyInput party)
    {
        flow.Pair("Nombre o razón social", party.Name);
        flow.Pair(Name(model, "6", party.DocumentTypeCode) is { Length: > 0 } type ? type : "Documento", party.DocumentNumber);
    }

    private static void Points(Flow flow, GreAddressInput origin, GreAddressInput? destination)
    {
        flow.Heading("Punto de partida");
        Point(flow, origin);
        if (destination is null)
        {
            flow.Heading("Punto de llegada");
            flow.Pair("Punto de llegada", "No se informa (emisor itinerante)");
            return;
        }

        flow.Heading("Punto de llegada");
        Point(flow, destination);
    }

    /// <summary>What the guides of import, export and foreign goods add: the port or airport, the net weight and the containers.</summary>
    private static void Customs(Flow flow, GrePrintModel model, CreateGreRequest request)
    {
        var customs = request.Customs;
        if (customs is null || request.MotiveCode is not ("08" or "09" or "19"))
        {
            return;
        }

        flow.Heading("Aduanas");
        if (!string.IsNullOrWhiteSpace(customs.PortCode))
        {
            flow.Pair(customs.PortType == "2" ? "Aeropuerto" : "Puerto", $"{customs.PortCode} - {customs.PortName}");
        }

        if (customs.WholeTransfer)
        {
            flow.Pair("Traslado", "Total de la declaración (DAM o DS)");
        }

        if (customs.ManifestContainers)
        {
            flow.Pair("Traslado", "En contenedores del manifiesto de carga");
        }

        if (customs.NetWeight is { } net)
        {
            flow.Pair("Peso neto", $"{Number(net)} KGM");
        }

        if (!string.IsNullOrWhiteSpace(customs.WeightNote))
        {
            flow.Pair("Sustento de la diferencia de peso", customs.WeightNote);
        }

        foreach (var (container, index) in (customs.Containers ?? []).Select((c, i) => (c, i)))
        {
            flow.Pair($"Contenedor {index + 1}", string.IsNullOrWhiteSpace(container.Seal) ? container.Number : $"{container.Number} - precinto {container.Seal}");
        }
    }

    private static void Point(Flow flow, GreAddressInput point)
    {
        flow.Pair("Ubigeo", point.UbigeoCode);
        if (!string.IsNullOrWhiteSpace(point.Address))
        {
            flow.Pair("Dirección", point.Address);
        }

        if (!string.IsNullOrWhiteSpace(point.EstablishmentCode))
        {
            flow.Pair("Establecimiento anexo", $"{point.EstablishmentRuc} - {point.EstablishmentCode}");
        }
    }

    private static void Vehicles(Flow flow, GreVehicleInput? principal, IReadOnlyList<GreVehicleInput>? secondaries)
    {
        if (principal is not null)
        {
            flow.Pair("Vehículo principal", VehicleText(principal));
        }

        foreach (var (vehicle, index) in (secondaries ?? []).Select((v, i) => (v, i)))
        {
            flow.Pair($"Vehículo secundario {index + 1}", VehicleText(vehicle));
        }
    }

    private static string VehicleText(GreVehicleInput vehicle) =>
        string.IsNullOrWhiteSpace(vehicle.CirculationCard) ? $"Placa {vehicle.Plate}" : $"Placa {vehicle.Plate} - Tarjeta de circulación {vehicle.CirculationCard}";

    private static void Drivers(Flow flow, GrePrintModel model, GreDriverInput? principal, IReadOnlyList<GreDriverInput>? secondaries)
    {
        if (principal is not null)
        {
            flow.Pair("Conductor principal", DriverText(model, principal));
        }

        foreach (var (driver, index) in (secondaries ?? []).Select((d, i) => (d, i)))
        {
            flow.Pair($"Conductor secundario {index + 1}", DriverText(model, driver));
        }
    }

    private static string DriverText(GrePrintModel model, GreDriverInput driver) =>
        $"{driver.FirstNames} {driver.LastNames} - {Name(model, "6", driver.DocumentTypeCode)} {driver.DocumentNumber} - Licencia {driver.LicenseNumber}";

    private static void Goods(Flow flow, IReadOnlyList<GreGoodInput>? goods)
    {
        if (goods is not { Count: > 0 })
        {
            return;
        }

        flow.Heading("Bienes a trasladar");
        flow.Row(true, "N.º", "Cantidad", "Unidad", "Descripción", "Código");
        var order = 1;
        foreach (var good in goods)
        {
            flow.Row(false, order++.ToString(CultureInfo.InvariantCulture), Number(good.Quantity), good.UnitCode, good.Description, good.Code ?? string.Empty);
        }
    }

    private static void Related(Flow flow, GrePrintModel model, IReadOnlyList<GreRelatedDocumentInput>? documents)
    {
        if (documents is not { Count: > 0 })
        {
            return;
        }

        flow.Heading("Documentos relacionados");
        foreach (var document in documents)
        {
            var issuer = string.IsNullOrWhiteSpace(document.IssuerRuc) ? string.Empty : $" - emisor RUC {document.IssuerRuc}";
            flow.Pair(Name(model, "61", document.TypeCode) is { Length: > 0 } type ? type : document.TypeCode, document.Number + issuer);
        }
    }

    // ---------- footer: the answer of SUNAT and the QR ----------

    private static void Footer(Flow flow, GrePrintModel model, string number)
    {
        flow.Ensure(QrSize + 40);
        flow.Heading("Constancia de SUNAT");
        var top = flow.Y;
        var page = flow.Page;
        var qrBottom = top - QrSize - 4;
        var textX = Left;
        if (model.QrUrl is { Length: > 0 } url)
        {
            textX = PdfQr.Draw(page, url, Left, qrBottom, QrSize, QuietZone) + 10;
        }

        var y = top - 10;
        foreach (var line in Lines(model, number))
        {
            foreach (var wrapped in Flow.Wrap(line.Text, line.Font, 8, Right - textX))
            {
                page.Text(line.Font, 8, textX, y, wrapped);
                y -= 10;
            }
        }

        flow.Y = Math.Min(qrBottom, y) - 8;
    }

    private static IEnumerable<(PdfFont Font, string Text)> Lines(GrePrintModel model, string number)
    {
        yield return (PdfFont.Bold, StateText(model.State));
        if (model.CdrResponseCode is { } code)
        {
            yield return (PdfFont.Regular, $"Respuesta de SUNAT: {model.CdrDescription} (código {code.ToString(CultureInfo.InvariantCulture)})");
        }

        foreach (var observation in model.Observations)
        {
            yield return (PdfFont.Regular, $"Observación {observation.Code}: {observation.Message}");
        }

        if (model.ProcessedAt is { } processed)
        {
            yield return (PdfFont.Regular, $"Fecha de la respuesta: {processed.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)} (hora de Lima)");
        }

        yield return (PdfFont.Bold, $"Para sustentar el traslado: RUC del remitente, serie y número de la guía");
        yield return (PdfFont.Regular, model.QrUrl is null
            ? $"Esta representación no lleva código QR: el QR lo entrega SUNAT con la constancia de la guía aceptada. La guía se identifica por el RUC {model.IssuerRuc}, la serie y el número {number}."
            : $"Código QR entregado por SUNAT en la constancia. RUC {model.IssuerRuc}, serie y número {number}.");
        yield return (PdfFont.Bold, "Resumen (hash):");
        yield return (PdfFont.Regular, model.DigestValue);
    }

    private static string StateText(GreState state) => state switch
    {
        GreState.Accepted => "Guía aceptada por SUNAT",
        GreState.AcceptedWithObservations => "Guía aceptada por SUNAT con observaciones",
        GreState.Rejected => "Guía rechazada por SUNAT: no tiene validez",
        GreState.Failed => "Guía no procesada por SUNAT: no tiene validez",
        GreState.Pending => "Guía enviada a SUNAT, a la espera de su constancia: aún no sustenta el traslado",
        _ => "Guía preparada, aún no enviada a SUNAT: no sustenta el traslado",
    };

    private static string Name(GrePrintModel model, string catalogue, string? code) =>
        code is not null && model.Names.TryGetValue(catalogue, out var names) && names.TryGetValue(code, out var name) ? name : string.Empty;

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>The pages of the document and the cursor: it opens a page with the header when the next block does not fit.</summary>
    private sealed class Flow(PdfWriter pdf, GrePrintModel model, string denomination, string number)
    {
        private static readonly double[] Columns = [Left, Left + 28, Left + 80, Left + 120, Left + 400, Right];

        public PdfPage Page { get; private set; } = null!;

        public double Y { get; set; }

        public static IEnumerable<string> Wrap(string text, PdfFont font, double size, double width)
        {
            var line = string.Empty;
            foreach (var word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Helvetica.Width(font, candidate, size) > width)
                {
                    yield return line;
                    line = word;
                    continue;
                }

                // A single word wider than the line (a long hash or URL) is cut where it no longer fits.
                while (Helvetica.Width(font, candidate, size) > width && candidate.Length > 1)
                {
                    var cut = candidate.Length;
                    while (cut > 1 && Helvetica.Width(font, candidate[..cut], size) > width)
                    {
                        cut--;
                    }

                    yield return candidate[..cut];
                    candidate = candidate[cut..];
                }

                line = candidate;
            }

            if (line.Length > 0)
            {
                yield return line;
            }
        }

        public void NewPage(bool first)
        {
            Page = pdf.AddPage();
            if (!IsValid(model.State))
            {
                // Drawn first and in light gray so the printed data stay readable underneath.
                Page.CenteredRotatedText(PdfFont.Bold, 90, 0.88, 45, PdfWriter.PageWidth / 2, PdfWriter.PageHeight / 2, "SIN VALIDEZ");
            }

            var top = PdfWriter.PageHeight - Margin;
            Page.Text(PdfFont.Bold, 12, Left, top - 12, model.IssuerName);
            var cursor = top - 26;
            foreach (var line in Wrap(model.IssuerAddress, 300, 8).Take(3))
            {
                Page.Text(PdfFont.Regular, 8, Left, cursor, line);
                cursor -= 10;
            }

            const double boxWidth = 210;
            const double boxHeight = 70;
            var boxLeft = Right - boxWidth;
            Page.StrokeRectangle(boxLeft, top - boxHeight, boxWidth, boxHeight, 1);
            Centered(PdfFont.Bold, 10, boxLeft + (boxWidth / 2), top - 18, $"RUC {model.IssuerRuc}");
            var title = Wrap(denomination, PdfFont.Bold, 9, boxWidth - 12).ToList();
            var titleY = top - 34;
            foreach (var part in title)
            {
                Centered(PdfFont.Bold, 9, boxLeft + (boxWidth / 2), titleY, part);
                titleY -= 11;
            }

            Centered(PdfFont.Bold, 11, boxLeft + (boxWidth / 2), top - boxHeight + 8, number);
            Y = top - boxHeight - 16;
            if (!first)
            {
                Page.Text(PdfFont.Regular, 8, Left, Y, $"Continuación de la guía {number}");
                Y -= 14;
            }
        }

        private static bool IsValid(GreState state) => state is GreState.Accepted or GreState.AcceptedWithObservations;

        private void Centered(PdfFont font, double size, double centerX, double y, string text) =>
            Page.Text(font, size, centerX - (Helvetica.Width(font, text, size) / 2), y, text);

        private static IEnumerable<string> Wrap(string text, double width, double size) => Wrap(text, PdfFont.Regular, size, width);

        public void Ensure(double height)
        {
            if (Y - height < Bottom)
            {
                NewPage(first: false);
            }
        }

        public void Heading(string title)
        {
            Ensure(40);
            Y -= 6;
            Page.Text(PdfFont.Bold, 9, Left, Y, title);
            Page.Line(Left, Y - 3, Right, Y - 3);
            Y -= 15;
        }

        public void Pair(string label, string value)
        {
            var lines = Wrap(value, PdfFont.Regular, 8, ContentWidth - LabelWidth).ToList();
            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            Ensure((lines.Count * 10) + 4);
            Page.Text(PdfFont.Bold, 8, Left, Y, label + ":");
            foreach (var line in lines)
            {
                Page.Text(PdfFont.Regular, 8, Left + LabelWidth, Y, line);
                Y -= 10;
            }

            Y -= 2;
        }

        public void Row(bool header, string order, string quantity, string unit, string description, string code)
        {
            var font = header ? PdfFont.Bold : PdfFont.Regular;
            var lines = Wrap(description, font, 8, Columns[4] - Columns[3] - 4).ToList();
            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            Ensure((lines.Count * 10) + 6);
            if (header)
            {
                Page.Line(Left, Y + 9, Right, Y + 9);
            }

            Page.Text(font, 8, Columns[0] + 2, Y, order);
            Page.TextRight(font, 8, Columns[2] - 6, Y, quantity);
            Page.Text(font, 8, Columns[2] + 2, Y, unit);
            Page.Text(font, 8, Columns[4] + 2, Y, code.Length > 22 ? code[..22] : code);
            foreach (var line in lines)
            {
                Page.Text(font, 8, Columns[3], Y, line);
                Y -= 10;
            }

            Page.Line(Left, Y + 7, Right, Y + 7, header ? 0.8 : 0.25);
            Y -= 2;
        }

        public byte[] Finish(string title) => pdf.ToBytes(title);
    }
}
