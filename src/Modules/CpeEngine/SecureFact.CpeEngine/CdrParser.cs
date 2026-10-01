using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine;

internal sealed partial class CdrParser(ICpePackager packager) : ICdrParser
{
    private static readonly XNamespace Ar = "urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private const int MaxCharacters = 1_000_000;

    public Result<CdrInfo> ParseZip(byte[] cdrZip)
    {
        var unzipped = packager.Unzip(cdrZip);
        return unzipped.IsSuccess ? Parse(unzipped.Value.Content) : unzipped.Error;
    }

    public Result<CdrInfo> Parse(string cdrXml)
    {
        if (string.IsNullOrWhiteSpace(cdrXml) || cdrXml.Length > MaxCharacters)
        {
            return Bad("El CDR está vacío o excede el tamaño permitido.");
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(cdrXml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return Bad("El CDR no es un XML bien formado.");
        }

        var root = document.Root;
        if (root is null || root.Name != Ar + "ApplicationResponse")
        {
            return Bad("El documento no es un ApplicationResponse UBL.");
        }

        var responses = root.Elements(Cac + "DocumentResponse").ToList();
        if (responses.Count != 1)
        {
            return Bad("Se esperaba exactamente un cac:DocumentResponse.");
        }

        var response = responses[0].Element(Cac + "Response");
        var processId = Text(root.Element(Cbc + "ID"));
        var (receivedDate, receivedTime) = ParseReceived(Text(root.Element(Cbc + "IssueDate")), Text(root.Element(Cbc + "IssueTime")));
        var responseDate = ParseDate(Text(root.Element(Cbc + "ResponseDate")));
        var responseTime = ParseTime(Text(root.Element(Cbc + "ResponseTime")));
        var sunat = PartyId(root.Element(Cac + "SenderParty"));
        var taxpayer = PartyId(root.Element(Cac + "ReceiverParty"));
        var referenceId = Text(response?.Element(Cbc + "ReferenceID"));
        var codeText = Text(response?.Element(Cbc + "ResponseCode"));
        var description = Text(response?.Element(Cbc + "Description"));

        if (processId.Length == 0 || responseDate is null || responseTime is null
            || sunat.Length == 0 || taxpayer.Length == 0 || referenceId.Length == 0)
        {
            return Bad("Faltan datos obligatorios en el CDR (identificador, fechas, horas, RUC o documento).");
        }

        if (!int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return Bad("El código de respuesta del CDR no es numérico.");
        }

        var observations = new List<CdrObservation>();
        foreach (var note in root.Elements(Cbc + "Note"))
        {
            var value = Text(note);
            var match = NotePattern().Match(value);
            if (match.Success)
            {
                observations.Add(new CdrObservation(match.Groups["code"].Value, match.Groups["text"].Value.Trim()));
            }
            else if (value.Length > 0)
            {
                // A note without a code is kept, never dropped: it may carry a message the taxpayer must see.
                observations.Add(new CdrObservation(string.Empty, value));
            }
        }

        return new CdrInfo(
            processId, receivedDate, receivedTime, responseDate.Value, responseTime.Value,
            sunat, taxpayer, referenceId, code, description, observations);
    }

    private static string PartyId(XElement? party) => Text(party?.Element(Cac + "PartyIdentification")?.Element(Cbc + "ID"));

    private static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;

    /// <summary>
    /// The manual shows a date plus a time. The beta service answers with a full timestamp in <c>IssueDate</c> (<c>2026-10-01T11:42:05</c>) and a
    /// meaningless <c>IssueTime</c> of 00:00:00; both shapes are accepted and the timestamp wins when present.
    /// </summary>
    private static (DateOnly? Date, TimeOnly? Time) ParseReceived(string date, string time)
    {
        if (date.Contains('T', StringComparison.Ordinal)
            && DateTime.TryParseExact(date, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return (DateOnly.FromDateTime(stamp), TimeOnly.FromDateTime(stamp));
        }

        var parsed = ParseDate(date);
        return (parsed, parsed is null ? null : ParseTime(time));
    }

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static TimeOnly? ParseTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static Error Bad(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "CDR no válido", detail);

    [GeneratedRegex(@"^\s*(?<code>\d{4})\s*-\s*(?<text>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex NotePattern();
}
