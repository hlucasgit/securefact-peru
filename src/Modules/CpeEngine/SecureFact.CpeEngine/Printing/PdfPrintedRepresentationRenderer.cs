using System.Globalization;
using QRCoder;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Printing;

/// <summary>
/// Printed representation (A4 PDF) of an electronic invoice or receipt. Content follows the "información mínima de la representación
/// impresa" columns of the SUNAT annexes (RS 114-2019, Annex II for receipts, S21): denomination instead of the document-type code, "RUC"
/// before the number, currency sign, the totals by operation type, the total amount and the legend that this is the printed
/// representation. The QR follows Anexo 6 §6.4 (S19): QR Code 2005, error correction Q, UTF-8, 1 mm quiet zone, black, bottom of the page,
/// at most 6 × 6 cm.
/// </summary>
internal sealed class PdfPrintedRepresentationRenderer : IPrintedRepresentationRenderer
{
    private const double Margin = 36;
    private const double Left = Margin;
    private const double Right = PdfWriter.PageWidth - Margin;
    private const double Top = PdfWriter.PageHeight - Margin;
    private const double QrSize = 100;          // points: 3.5 cm, well under the 6 cm ceiling
    private const double QuietZone = 2.835;     // 1 mm in points
    private const double FooterHeight = 255;    // room kept above the bottom margin for the amount in words, the totals and the QR block
    private const int MaxLines = 2000;
    private const int MaxDescriptionLines = 6;

    private static readonly double[] Columns = [Left, 72, 108, 350, 410, 470, 515, Right];

    public Result<byte[]> Render(PrintedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var isNote = document.DocumentTypeCode is "07" or "08";
        if (document.DocumentTypeCode is not ("01" or "03" or "07" or "08") || document.Lines.Count is 0 or > MaxLines || string.IsNullOrWhiteSpace(document.QrPayload)
            || (isNote && document.Note is null))
        {
            return Error.Validation(ErrorCodes.CpeInvalidDocument, "Documento no imprimible", "Se requiere una factura, boleta o nota (con el documento que modifica) con líneas y datos del QR.");
        }

        var (denomination, legend) = document.DocumentTypeCode switch
        {
            "01" => ("FACTURA ELECTRÓNICA", "Representación impresa de la factura electrónica"),
            "03" => ("BOLETA DE VENTA ELECTRÓNICA", "Representación impresa de la boleta de venta electrónica"),
            "07" => ("NOTA DE CRÉDITO ELECTRÓNICA", "Representación impresa de la nota de crédito electrónica"),
            _ => ("NOTA DE DÉBITO ELECTRÓNICA", "Representación impresa de la nota de débito electrónica"),
        };
        var number = $"{document.Series}-{document.Number.ToString(CultureInfo.InvariantCulture)}";
        var symbol = Symbol(document.Currency);

        var pdf = new PdfWriter();
        var page = NewPage(pdf, document, denomination, number, firstPage: true, out var y);

        foreach (var line in document.Lines)
        {
            var description = Wrap(line.Description, Columns[3] - Columns[2] - 6, 8).Take(MaxDescriptionLines).ToList();
            var height = (description.Count * 10) + 4;
            if (y - height < Margin + 40)
            {
                page = NewPage(pdf, document, denomination, number, firstPage: false, out y);
            }

            var baseline = y - 9;
            page.TextRight(PdfFont.Regular, 8, Columns[1] - 6, baseline, Quantity(line.Quantity));
            page.Text(PdfFont.Regular, 8, Columns[1] + 2, baseline, Unit(line.UnitCode));
            for (var i = 0; i < description.Count; i++)
            {
                page.Text(PdfFont.Regular, 8, Columns[2] + 2, baseline - (i * 10), description[i]);
            }

            page.TextRight(PdfFont.Regular, 8, Columns[4] - 4, baseline, Money(line.UnitValue, 2, extraDecimals: true));
            page.TextRight(PdfFont.Regular, 8, Columns[5] - 4, baseline, line.UnitPriceIncludingTaxes is { } price ? Money(price, 2, extraDecimals: true) : "-");
            page.TextRight(PdfFont.Regular, 8, Columns[6] - 4, baseline, Money(line.TaxAmount));
            page.TextRight(PdfFont.Regular, 8, Columns[7] - 2, baseline, Money(line.LineExtensionAmount));
            y -= height;
            page.Line(Left, y, Right, y, 0.25);
        }

        if (document.Installments is { Count: > 0 } installments)
        {
            // Payment form on credit: the net pending amount and the installments, as a compact list that follows the lines and paginates like them.
            if (y - 30 < Margin + FooterHeight)
            {
                page = NewPage(pdf, document, denomination, number, firstPage: false, out y);
            }

            y -= 12;
            page.Text(PdfFont.Bold, 8, Left, y, "Forma de pago: Crédito");
            page.Text(PdfFont.Regular, 8, Left + 130, y, $"Monto neto pendiente de pago: {symbol} {Money(installments.Sum(i => i.Amount))}");
            y -= 11;
            if (document.InitialPayment > 0)
            {
                page.Text(PdfFont.Regular, 8, Left + 130, y, $"Entrega inicial (pagada a la emisión): {symbol} {Money(document.InitialPayment)}");
                y -= 11;
            }

            foreach (var installment in installments)
            {
                if (y - 11 < Margin + FooterHeight)
                {
                    page = NewPage(pdf, document, denomination, number, firstPage: false, out y);
                    y -= 4;
                }

                page.Text(PdfFont.Regular, 8, Left + 10, y, $"Cuota {installment.Number.ToString(CultureInfo.InvariantCulture)}");
                page.Text(PdfFont.Regular, 8, Left + 70, y, $"Vence: {installment.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
                page.TextRight(PdfFont.Regular, 8, Left + 260, y, $"{symbol} {Money(installment.Amount)}");
                y -= 11;
            }
        }

        if (document.AdditionalInformation is { Count: > 0 } information)
        {
            y -= 4;
            foreach (var text in information.SelectMany(i => Wrap(i, Right - Left - 4, 8)))
            {
                if (y - 11 < Margin + FooterHeight)
                {
                    page = NewPage(pdf, document, denomination, number, firstPage: false, out y);
                    y -= 4;
                }

                page.Text(PdfFont.Regular, 8, Left, y - 8, text);
                y -= 11;
            }
        }

        if (y < Margin + FooterHeight)
        {
            page = NewPage(pdf, document, denomination, number, firstPage: false, out y);
        }

        Footer(page, document, legend, symbol, y);
        return pdf.ToBytes($"{denomination} {number}");
    }

    private static PdfPage NewPage(PdfWriter pdf, PrintedDocument document, string denomination, string number, bool firstPage, out double y)
    {
        var page = pdf.AddPage();
        var top = Top;

        if (document.Voided)
        {
            // Drawn first and in light gray so the printed data stay readable underneath.
            page.CenteredRotatedText(PdfFont.Bold, 110, 0.85, 45, PdfWriter.PageWidth / 2, PdfWriter.PageHeight / 2, "ANULADO");
        }

        // Issuer block (left) and the document box (right).
        page.Text(PdfFont.Bold, 12, Left, top - 12, document.IssuerName);
        var cursor = top - 26;
        if (!string.IsNullOrWhiteSpace(document.IssuerTradeName))
        {
            page.Text(PdfFont.Regular, 9, Left, cursor, document.IssuerTradeName);
            cursor -= 12;
        }

        foreach (var addressLine in Wrap(document.IssuerAddress, 300, 8).Take(3))
        {
            page.Text(PdfFont.Regular, 8, Left, cursor, addressLine);
            cursor -= 10;
        }

        const double boxWidth = 190;
        const double boxHeight = 66;
        var boxLeft = Right - boxWidth;
        page.StrokeRectangle(boxLeft, top - boxHeight, boxWidth, boxHeight, 1);
        page.Text(PdfFont.Bold, 10, boxLeft + ((boxWidth - Helvetica.Width(PdfFont.Bold, $"RUC {document.IssuerRuc}", 10)) / 2), top - 18, $"RUC {document.IssuerRuc}");
        page.Text(PdfFont.Bold, 10, boxLeft + ((boxWidth - Helvetica.Width(PdfFont.Bold, denomination, 10)) / 2), top - 36, denomination);
        page.Text(PdfFont.Bold, 11, boxLeft + ((boxWidth - Helvetica.Width(PdfFont.Bold, number, 11)) / 2), top - 54, number);

        y = top - boxHeight - 14;
        if (document.Voided)
        {
            page.TextRight(PdfFont.Bold, 12, Right, y, "ANULADO");
        }

        if (firstPage)
        {
            page.Text(PdfFont.Bold, 8, Left, y, "Fecha de emisión:");
            page.Text(PdfFont.Regular, 8, Left + 80, y, document.IssueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            page.Text(PdfFont.Bold, 8, Left + 180, y, "Moneda:");
            page.Text(PdfFont.Regular, 8, Left + 220, y, $"{AmountInWords.CurrencyName(document.Currency)} ({Symbol(document.Currency)})");
            y -= 12;

            if (document.BuyerDocumentNumber is not null)
            {
                page.Text(PdfFont.Bold, 8, Left, y, "Adquirente:");
                page.Text(PdfFont.Regular, 8, Left + 80, y, document.BuyerName ?? string.Empty);
                y -= 12;
                var typeLabel = $"{document.BuyerDocumentTypeName}:";
                page.Text(PdfFont.Bold, 8, Left, y, typeLabel);
                page.Text(PdfFont.Regular, 8, Math.Max(Left + 80, Left + Helvetica.Width(PdfFont.Bold, typeLabel, 8) + 6), y, document.BuyerDocumentNumber);
                y -= 12;
                if (!string.IsNullOrWhiteSpace(document.BuyerAddress))
                {
                    page.Text(PdfFont.Bold, 8, Left, y, "Dirección:");
                    page.Text(PdfFont.Regular, 8, Left + 80, y, Wrap(document.BuyerAddress, 420, 8).First());
                    y -= 12;
                }
            }

            if (document.Note is { } note)
            {
                page.Text(PdfFont.Bold, 8, Left, y, "Documento que modifica:");
                page.Text(PdfFont.Regular, 8, Left + 110, y, note.ReferencedDocument);
                y -= 12;
                page.Text(PdfFont.Bold, 8, Left, y, "Motivo:");
                foreach (var reasonLine in Wrap(note.Reason, Right - Left - 110, 8).Take(4))
                {
                    page.Text(PdfFont.Regular, 8, Left + 110, y, reasonLine);
                    y -= 10;
                }

                y -= 2;
            }

            y -= 6;
        }
        else
        {
            page.Text(PdfFont.Regular, 8, Left, y, $"{denomination} {number} (continuación)");
            y -= 14;
        }

        // Table header.
        page.FillRectangle(Left, y - 12, Right - Left, 14, 0.9);
        page.TextRight(PdfFont.Bold, 7.5, Columns[1] - 6, y - 8, "Cant.");
        page.Text(PdfFont.Bold, 7.5, Columns[1] + 2, y - 8, "Unid.");
        page.Text(PdfFont.Bold, 7.5, Columns[2] + 2, y - 8, "Descripción");
        page.TextRight(PdfFont.Bold, 7.5, Columns[4] - 4, y - 8, "V. unitario");
        page.TextRight(PdfFont.Bold, 7.5, Columns[5] - 4, y - 8, "P. unitario");
        page.TextRight(PdfFont.Bold, 7.5, Columns[6] - 4, y - 8, "IGV");
        page.TextRight(PdfFont.Bold, 7.5, Columns[7] - 2, y - 8, "Valor venta");
        y -= 14;
        return page;
    }

    private static void Footer(PdfPage page, PrintedDocument document, string legend, string symbol, double y)
    {
        var totals = document.Totals;
        var top = y - 14;
        var words = AmountInWords.Describe(totals.TotalAmount, document.Currency);
        page.Text(PdfFont.Bold, 8, Left, top, words);

        // Totals, right-aligned; operation types that do not apply are not printed (the rule marks them "solo de corresponder").
        var rows = new List<(string Label, decimal Amount, bool Bold)>();
        if (totals.TaxedAmount != 0)
        {
            rows.Add(("Op. gravadas", totals.TaxedAmount, false));
        }

        if (totals.ExemptAmount != 0)
        {
            rows.Add(("Op. exoneradas", totals.ExemptAmount, false));
        }

        if (totals.ExportAmount != 0)
        {
            rows.Add(("Op. exportación", totals.ExportAmount, false));
        }

        if (totals.UnaffectedAmount != 0)
        {
            rows.Add(("Op. inafectas", totals.UnaffectedAmount, false));
        }

        if (totals.FreeAmount != 0)
        {
            rows.Add(("Op. gratuitas", totals.FreeAmount, false));
        }

        if (totals.IgvAmount != 0 || totals.IvapAmount == 0)
        {
            rows.Add(("IGV", totals.IgvAmount, false));
        }

        if (totals.IvapAmount != 0)
        {
            rows.Add(("IVAP", totals.IvapAmount, false));
        }

        if (totals.OtherCharges != 0)
        {
            rows.Add(("Otros cargos", totals.OtherCharges, false));
        }

        if (totals.OtherDiscounts != 0)
        {
            rows.Add(("Otros descuentos", -totals.OtherDiscounts, false));
        }

        rows.Add(("IMPORTE TOTAL", totals.TotalAmount, true));

        var rowY = top - 18;
        foreach (var (label, amount, bold) in rows)
        {
            var font = bold ? PdfFont.Bold : PdfFont.Regular;
            page.TextRight(font, 9, Right - 110, rowY, label);
            page.TextRight(font, 9, Right, rowY, $"{symbol} {Money(amount)}");
            rowY -= 13;
        }

        // QR at the bottom left, with the summary value and the legend beside it.
        var qrY = Margin;
        var next = DrawQr(page, document.QrPayload, Left, qrY);
        page.Text(PdfFont.Regular, 7, next + 8, qrY + QrSize - 10, legend);
        page.Text(PdfFont.Bold, 7, next + 8, qrY + QrSize - 24, "Resumen (hash):");
        var hashY = qrY + QrSize - 34;
        foreach (var chunk in Chunk(document.DigestValue, 48))
        {
            page.Text(PdfFont.Regular, 7, next + 8, hashY, chunk);
            hashY -= 9;
        }
    }

    /// <summary>Draws the QR as vector squares and returns the x where it ends. Dark runs on a row are merged into one rectangle.</summary>
    private static double DrawQr(PdfPage page, string payload, double x, double y)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q, forceUtf8: true, utf8BOM: false, eciMode: QRCodeGenerator.EciMode.Utf8);
        var matrix = data.ModuleMatrix;

        // QRCoder keeps a 4-module quiet zone inside the matrix; the symbol itself is what lies inside it.
        const int padding = 4;
        var modules = matrix.Count - (2 * padding);
        var module = (QrSize - (2 * QuietZone)) / modules;
        var originX = x + QuietZone;
        var originY = y + QuietZone;

        for (var row = 0; row < modules; row++)
        {
            var column = 0;
            while (column < modules)
            {
                if (!matrix[row + padding][column + padding])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < modules && matrix[row + padding][column + padding])
                {
                    column++;
                }

                page.FillRectangle(originX + (start * module), originY + ((modules - 1 - row) * module), (column - start) * module, module);
            }
        }

        return x + QrSize;
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    private static List<string> Wrap(string text, double width, double size)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Helvetica.Width(PdfFont.Regular, candidate, size) <= width || current.Length == 0)
            {
                current = candidate;
                if (Helvetica.Width(PdfFont.Regular, current, size) > width)
                {
                    // A single word wider than the column is cut so it never runs over the next column.
                    while (Helvetica.Width(PdfFont.Regular, current, size) > width && current.Length > 1)
                    {
                        var cut = current.Length - 1;
                        while (cut > 1 && Helvetica.Width(PdfFont.Regular, current[..cut], size) > width)
                        {
                            cut--;
                        }

                        lines.Add(current[..cut]);
                        current = current[cut..];
                    }
                }
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }

    private static string Symbol(string currency) => currency switch
    {
        "PEN" => "S/",
        "USD" => "US$",
        "EUR" => "€",
        _ => currency,
    };

    private static string Unit(string? code) => code is null or "NIU" or "ZZ" ? string.Empty : code;

    private static string Quantity(decimal value) => value == Math.Truncate(value) ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Money(decimal value, int decimals = 2, bool extraDecimals = false) =>
        value.ToString(extraDecimals ? "0.00####" : "N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
