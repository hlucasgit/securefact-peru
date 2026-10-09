using System.Xml;
using System.Xml.Linq;

namespace SecureFact.Gre.Printing;

/// <summary>
/// Finds the address that SUNAT gives in the CDR of an accepted guide to build the QR code (art. 35 of RS 255-2015/SUNAT as amended by RS 123-2022/SUNAT: «con la CDR con estado aceptada se remite
/// al emisor electrónico la información necesaria para generar el código QR»). The published sources do not say which element of the CDR carries it, so nothing is assumed about the element: any
/// element of the CDR whose whole text is an <c>https</c> address of a host of <c>sunat.gob.pe</c> is taken (ADR-058). If the CDR has none, the printed representation has no QR.
/// </summary>
internal static class GreQrUrl
{
    private const int MaxLength = 2000;

    /// <returns>The address, or null when the CDR carries none.</returns>
    public static string? Find(string cdrXml)
    {
        if (string.IsNullOrWhiteSpace(cdrXml))
        {
            return null;
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(cdrXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        foreach (var element in document.Descendants().Where(e => !e.HasElements))
        {
            var text = element.Value.Trim();
            if (text.Length is > 0 and <= MaxLength && IsSunatAddress(text))
            {
                return text;
            }
        }

        return null;
    }

    private static bool IsSunatAddress(string text) =>
        !text.Any(char.IsWhiteSpace)
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "sunat.gob.pe" || uri.Host.EndsWith(".sunat.gob.pe", StringComparison.Ordinal));
}
