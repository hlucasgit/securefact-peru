using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SecureFact.Platform.Printing;

public enum PdfFont
{
    Regular,
    Bold,
}

/// <summary>
/// A deliberately small PDF 1.4 writer: A4 pages, the two standard Helvetica fonts (no font files to ship or license), text, lines and
/// filled rectangles. Output is deterministic (no timestamps or random ids), which makes it testable and cacheable.
/// </summary>
public sealed class PdfWriter
{
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;

    private readonly List<PdfPage> _pages = [];

    public PdfPage AddPage()
    {
        var page = new PdfPage();
        _pages.Add(page);
        return page;
    }

    public int PageCount => _pages.Count;

    public byte[] ToBytes(string title)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));

        void BeginObject(int number)
        {
            offsets.Add(output.Position);
            Write($"{number.ToString(CultureInfo.InvariantCulture)} 0 obj\n");
        }

        Write("%PDF-1.4\n%âãÏÓ\n");

        // 1 catalog, 2 page tree, 3/4 fonts, 5 info, then (page, content) pairs.
        var pageObjects = Enumerable.Range(0, _pages.Count).Select(i => 6 + (i * 2)).ToList();

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObject(2);
        Write($"<< /Type /Pages /Count {_pages.Count.ToString(CultureInfo.InvariantCulture)} /Kids [{string.Join(' ', pageObjects.Select(n => $"{n.ToString(CultureInfo.InvariantCulture)} 0 R"))}] >>\nendobj\n");

        BeginObject(3);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        BeginObject(4);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        BeginObject(5);
        Write($"<< /Title ({Escape(title)}) /Producer (SecureFact) >>\nendobj\n");

        for (var i = 0; i < _pages.Count; i++)
        {
            var page = pageObjects[i];
            BeginObject(page);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(PageWidth)} {Number(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {(page + 1).ToString(CultureInfo.InvariantCulture)} 0 R >>\nendobj\n");

            var raw = Encoding.Latin1.GetBytes(_pages[i].Content.ToString());
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            BeginObject(page + 1);
            Write($"<< /Length {compressed.Length.ToString(CultureInfo.InvariantCulture)} /Filter /FlateDecode >>\nstream\n");
            output.Write(compressed.ToArray());
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        var count = offsets.Count + 1;
        Write($"xref\n0 {count.ToString(CultureInfo.InvariantCulture)}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset.ToString("0000000000", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        Write($"trailer\n<< /Size {count.ToString(CultureInfo.InvariantCulture)} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Escapes a string for a PDF literal and keeps only characters WinAnsiEncoding can show (others become '?').</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '(' or ')')
            {
                builder.Append('\\').Append(c);
                continue;
            }

            builder.Append(c switch
            {
                '\r' or '\n' or '\t' => ' ',
                < ' ' => '?',
                > 'ÿ' => '?',
                _ => c,
            });
        }

        return builder.ToString();
    }
}

public sealed class PdfPage
{
    public StringBuilder Content { get; } = new();

    public void Text(PdfFont font, double size, double x, double y, string text) =>
        Content.Append(CultureInfo.InvariantCulture, $"BT /{(font == PdfFont.Bold ? "F2" : "F1")} {PdfWriter.Number(size)} Tf {PdfWriter.Number(x)} {PdfWriter.Number(y)} Td ({PdfWriter.Escape(text)}) Tj ET\n");

    public void TextRight(PdfFont font, double size, double rightX, double y, string text) =>
        Text(font, size, rightX - Helvetica.Width(font, text, size), y, text);

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5) =>
        Content.Append(CultureInfo.InvariantCulture, $"{PdfWriter.Number(width)} w {PdfWriter.Number(x1)} {PdfWriter.Number(y1)} m {PdfWriter.Number(x2)} {PdfWriter.Number(y2)} l S\n");

    public void StrokeRectangle(double x, double y, double width, double height, double lineWidth = 0.5) =>
        Content.Append(CultureInfo.InvariantCulture, $"{PdfWriter.Number(lineWidth)} w {PdfWriter.Number(x)} {PdfWriter.Number(y)} {PdfWriter.Number(width)} {PdfWriter.Number(height)} re S\n");

    /// <summary>Draws text in a gray level, rotated counter-clockwise by <paramref name="degrees"/> and centred on a point; the fill colour goes back to black afterwards.</summary>
    public void CenteredRotatedText(PdfFont font, double size, double gray, double degrees, double centerX, double centerY, string text)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var half = Helvetica.Width(font, text, size) / 2;
        var x = centerX - (half * cos) + ((size * 0.35) * sin);
        var y = centerY - (half * sin) - ((size * 0.35) * cos);
        Content.Append(CultureInfo.InvariantCulture, $"{PdfWriter.Number(gray)} g BT /{(font == PdfFont.Bold ? "F2" : "F1")} {PdfWriter.Number(size)} Tf {cos:0.####} {sin:0.####} {-sin:0.####} {cos:0.####} {PdfWriter.Number(x)} {PdfWriter.Number(y)} Tm ({PdfWriter.Escape(text)}) Tj ET 0 g\n");
    }

    /// <summary>Fills a rectangle in black (gray 0) or in the given gray level (0 black, 1 white).</summary>
    public void FillRectangle(double x, double y, double width, double height, double gray = 0)
    {
        Content.Append(CultureInfo.InvariantCulture, $"{PdfWriter.Number(gray)} g {PdfWriter.Number(x)} {PdfWriter.Number(y)} {PdfWriter.Number(width)} {PdfWriter.Number(height)} re f 0 g\n");
    }
}

/// <summary>Advance widths (1/1000 em) of the standard Helvetica fonts for ASCII; accented Latin-1 letters take the width of their base letter.</summary>
public static class Helvetica
{
    private const string Printable = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

    private static readonly int[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015,
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        278, 278, 278, 469, 556, 333,
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,
        334, 260, 334, 584,
    ];

    private static readonly int[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975,
        722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        333, 278, 333, 584, 556, 333,
        556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500,
        389, 280, 389, 584,
    ];

    private const string Accented = "áàâäãéèêëíìîïóòôöõúùûüñçÁÀÂÄÃÉÈÊËÍÌÎÏÓÒÔÖÕÚÙÛÜÑÇ";
    private const string Bases = "aaaaaeeeeiiiiooooouuuuncAAAAAEEEEIIIIOOOOOUUUUNC";

    public static double Width(PdfFont font, string text, double size)
    {
        var table = font == PdfFont.Bold ? Bold : Regular;
        var total = 0;
        foreach (var c in text)
        {
            var key = c;
            var accent = Accented.IndexOf(c, StringComparison.Ordinal);
            if (accent >= 0)
            {
                key = Bases[accent];
            }

            var index = Printable.IndexOf(key, StringComparison.Ordinal);
            total += index >= 0 ? table[index] : 556;
        }

        return total * size / 1000.0;
    }
}

/// <summary>Draws a QR code on a page as vector squares, so that it prints sharp at any size. QR Code 2005, error correction Q, UTF-8 (S19, numeral 6.4.2).</summary>
public static class PdfQr
{
    /// <summary>Draws the QR in a square of <paramref name="size"/> points whose lower left corner is (<paramref name="x"/>, <paramref name="y"/>), leaving <paramref name="quietZone"/> points free on each side. Dark runs on a row are merged into one rectangle.</summary>
    /// <returns>The x where the square ends.</returns>
    public static double Draw(PdfPage page, string payload, double x, double y, double size, double quietZone)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(payload);
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.Q, forceUtf8: true, utf8BOM: false, eciMode: QRCoder.QRCodeGenerator.EciMode.Utf8);
        var matrix = data.ModuleMatrix;

        // QRCoder keeps a 4-module quiet zone inside the matrix; the symbol itself is what lies inside it.
        const int padding = 4;
        var modules = matrix.Count - (2 * padding);
        var module = (size - (2 * quietZone)) / modules;
        var originX = x + quietZone;
        var originY = y + quietZone;

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

        return x + size;
    }
}
