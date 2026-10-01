using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine;

/// <summary>
/// UBL 2.0 <c>VoidedDocuments</c> (comunicación de baja 1.0), element order of the official XSD (UBLPE-VoidedDocuments-1.0) and the
/// mandatory tags of sheet <c>Comunicación de Baja1_0</c> of the 2026-08-26 validation rules (S16). Covers invoices (01) and notes (07/08)
/// of invoices, whose series start with F (rule 2310); receipts and their notes are voided through the daily summary (status 3).
/// </summary>
internal sealed partial class VoidedDocumentsGenerator : IVoidedDocumentsGenerator
{
    private static readonly XNamespace Voided = "urn:sunat:names:specification:ubl:peru:schema:xsd:VoidedDocuments-1";
    private static readonly XNamespace Sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    [GeneratedRegex("^F[A-Z0-9]{3}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex InvoiceSeries();

    [GeneratedRegex("^[0-9]{11}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex RucPattern();

    public Result<VoidedDocument> Generate(VoidedData data)
    {
        if (Validate(data) is { } invalid)
        {
            return invalid;
        }

        var identifier = $"RA-{data.IssueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{data.Correlative.ToString(CultureInfo.InvariantCulture)}";
        var root = new XElement(
            Voided + "VoidedDocuments",
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ds", Ds),
            new XAttribute(XNamespace.Xmlns + "ext", Ext),
            new XAttribute(XNamespace.Xmlns + "sac", Sac),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.0"),
            new XElement(Cbc + "CustomizationID", "1.0"),
            new XElement(Cbc + "ID", identifier),
            new XElement(Cbc + "ReferenceDate", Date(data.ReferenceDate)),
            new XElement(Cbc + "IssueDate", Date(data.IssueDate)),
            new XElement(
                Cac + "Signature",
                new XElement(Cbc + "ID", $"Sign-{data.Ruc}"),
                new XElement(
                    Cac + "SignatoryParty",
                    new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", data.Ruc)),
                    new XElement(Cac + "PartyName", new XElement(Cbc + "Name", data.LegalName))),
                new XElement(
                    Cac + "DigitalSignatureAttachment",
                    new XElement(Cac + "ExternalReference", new XElement(Cbc + "URI", $"#Sign-{data.Ruc}")))),
            new XElement(
                Cac + "AccountingSupplierParty",
                new XElement(Cbc + "CustomerAssignedAccountID", data.Ruc),
                new XElement(Cbc + "AdditionalAccountID", IdentityDocuments.Ruc),
                new XElement(Cac + "Party", new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", data.LegalName)))));

        foreach (var line in data.Lines)
        {
            root.Add(new XElement(
                Sac + "VoidedDocumentsLine",
                new XElement(Cbc + "LineID", line.LineNumber.ToString(CultureInfo.InvariantCulture)),
                new XElement(Cbc + "DocumentTypeCode", line.DocumentTypeCode),
                new XElement(Sac + "DocumentSerialID", line.Series),
                new XElement(Sac + "DocumentNumberID", line.Number.ToString(CultureInfo.InvariantCulture)),
                new XElement(Sac + "VoidReasonDescription", line.Reason.Trim())));
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var buffer = new StringWriterUtf8();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }

        return new VoidedDocument(buffer.ToString(), identifier, $"{data.Ruc}-{identifier}");
    }

    private static Error? Validate(VoidedData data)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de comunicación de baja inválidos", detail);

        if (data is null || data.Lines is not { Count: > 0 })
        {
            return Invalid("La comunicación requiere al menos un documento.");
        }

        if (data.Lines.Count > IVoidedDocumentsGenerator.MaxLines)
        {
            return Invalid($"Una comunicación admite como máximo {IVoidedDocumentsGenerator.MaxLines} líneas; divídala en varias.");
        }

        if (!RucPattern().IsMatch(data.Ruc ?? string.Empty) || string.IsNullOrWhiteSpace(data.LegalName))
        {
            return Invalid("El RUC (11 dígitos) y la razón social del emisor son obligatorios.");
        }

        if (data.Correlative is < 1 or > 99999)
        {
            return Invalid("El correlativo debe estar entre 1 y 99999.");
        }

        if (data.IssueDate < data.ReferenceDate)
        {
            return Invalid("La fecha de generación no puede ser anterior a la fecha de emisión de los documentos (regla 2671).");
        }

        var seen = new HashSet<(string, string, long)>();
        for (var i = 0; i < data.Lines.Count; i++)
        {
            var line = data.Lines[i];
            var label = $"Línea {i + 1}";
            if (line.LineNumber != i + 1)
            {
                return Invalid($"{label}: los números de fila deben ser correlativos desde 1.");
            }

            if (line.DocumentTypeCode is not ("01" or "07" or "08"))
            {
                return Invalid($"{label}: solo se dan de baja facturas (01) y notas (07/08) de facturas; las boletas y sus notas se anulan por resumen diario.");
            }

            if (!InvoiceSeries().IsMatch(line.Series ?? string.Empty) || line.Number is < 1 or > 99_999_999)
            {
                return Invalid($"{label}: la serie debe ser F + 3 caracteres (regla 2310) y el número de 1 a 8 dígitos.");
            }

            if (!seen.Add((line.DocumentTypeCode, line.Series!, line.Number)))
            {
                return Invalid($"{label}: el documento {line.DocumentTypeCode} {line.Series}-{line.Number} está repetido (regla 2348).");
            }

            var reason = line.Reason?.Trim() ?? string.Empty;
            if (reason.Length is < 3 or > 100 || reason.Any(c => c is '\n' or '\r' or '\t'))
            {
                return Invalid($"{label}: el motivo de baja debe tener de 3 a 100 caracteres, sin saltos de línea ni tabulaciones.");
            }
        }

        return null;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class StringWriterUtf8 : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
