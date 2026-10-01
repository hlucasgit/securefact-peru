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
/// UBL 2.0 <c>SummaryDocuments</c> (daily summary of receipts, "Resumen Diario 1.1"). Element order follows the official XSD
/// (UBLPE-SummaryDocuments-1.0) and the mandatory tags of sheet <c>Resumen Diario1_1</c> of the 2026-08-26 validation rules (S16);
/// the 2018 guide (S18) gives the examples. Scope: receipts (03) and notes (07/08) of receipts that are added (status 1) with taxed, exempt and unaffected amounts in
/// the receipt's own currency. Notes, voids (status 3), modifications, free operations, exports, ISC, ICBPER and perception return
/// <c>SF-CPE-002</c> or are absent from the model, never silently dropped.
/// </summary>
internal sealed partial class SummaryDocumentGenerator : ISummaryDocumentGenerator
{
    private static readonly XNamespace Sum = "urn:sunat:names:specification:ubl:peru:schema:xsd:SummaryDocuments-1";
    private static readonly XNamespace Sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    private const decimal ReceiptIdentificationThreshold = 700m;
    private const decimal TotalTolerance = 0.05m;

    [GeneratedRegex("^B[A-Z0-9]{3}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ReceiptSeries();

    [GeneratedRegex("^[0-9]{11}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex RucPattern();

    public Result<SummaryDocument> Generate(SummaryData data)
    {
        if (Validate(data) is { } invalid)
        {
            return invalid;
        }

        var identifier = $"RC-{data.IssueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{data.Correlative.ToString(CultureInfo.InvariantCulture)}";
        var root = new XElement(
            Sum + "SummaryDocuments",
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ds", Ds),
            new XAttribute(XNamespace.Xmlns + "ext", Ext),
            new XAttribute(XNamespace.Xmlns + "sac", Sac),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.0"),
            new XElement(Cbc + "CustomizationID", "1.1"),
            new XElement(Cbc + "ID", identifier),
            new XElement(Cbc + "ReferenceDate", Date(data.ReferenceDate)),
            new XElement(Cbc + "IssueDate", Date(data.IssueDate)),
            SignatureInfo(data),
            new XElement(
                Cac + "AccountingSupplierParty",
                new XElement(Cbc + "CustomerAssignedAccountID", data.Ruc),
                new XElement(Cbc + "AdditionalAccountID", IdentityDocuments.Ruc),
                new XElement(Cac + "Party", new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", data.LegalName)))));

        foreach (var line in data.Lines)
        {
            root.Add(Line(line));
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var buffer = new StringWriterUtf8();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }

        return new SummaryDocument(buffer.ToString(), identifier, $"{data.Ruc}-{identifier}");
    }

    private static Error? Validate(SummaryData data)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de resumen inválidos", detail);
        static Error Unsupported(string detail) => Error.Validation(ErrorCodes.CpeUnsupported, "Resumen no soportado por el generador", detail);

        if (data is null || data.Lines is not { Count: > 0 })
        {
            return Invalid("El resumen requiere al menos un comprobante.");
        }

        if (data.Lines.Count > ISummaryDocumentGenerator.MaxLines)
        {
            return Invalid($"Un resumen admite como máximo {ISummaryDocumentGenerator.MaxLines} líneas; divídalo en bloques con correlativos distintos.");
        }

        if (!RucPattern().IsMatch(data.Ruc ?? string.Empty) || string.IsNullOrWhiteSpace(data.LegalName))
        {
            return Invalid("El RUC (11 dígitos) y la razón social del emisor son obligatorios.");
        }

        if (data.Correlative is < 1 or > 99999)
        {
            return Invalid("El correlativo del resumen debe estar entre 1 y 99999.");
        }

        if (data.IssueDate < data.ReferenceDate)
        {
            return Invalid("La fecha de generación del resumen no puede ser anterior a la fecha de emisión de los comprobantes.");
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

            if (!ReceiptSeries().IsMatch(line.Series ?? string.Empty) || line.Number is < 1 or > 99_999_999)
            {
                return Invalid($"{label}: serie (B + 3 caracteres) o número (1 a 8 dígitos) inválidos.");
            }

            if (line.DocumentTypeCode is not ("03" or "07" or "08"))
            {
                return Invalid($"{label}: el resumen solo informa boletas (03) y notas de crédito (07) o débito (08).");
            }

            if (line.Status is not ("1" or "3"))
            {
                return Invalid($"{label}: el estado debe ser 1 (adicionar) o 3 (anular); las modificaciones (2) aún no están soportadas.");
            }

            // The same document twice in one file is refused whatever the status (rules 3094, 3095, 3096).
            if (!seen.Add((line.DocumentTypeCode, line.Series!, line.Number)))
            {
                return Invalid($"{label}: el comprobante {line.DocumentTypeCode} {line.Series}-{line.Number} está repetido.");
            }

            var isNote = line.DocumentTypeCode is "07" or "08";
            if (isNote)
            {
                if (line.ReferencedDocumentTypeCode != "03" || !ReceiptSeries().IsMatch(line.ReferencedSeries ?? string.Empty) || line.ReferencedNumber is null or < 1 or > 99_999_999)
                {
                    return Invalid($"{label}: una nota del resumen debe modificar una boleta (03) identificada por serie B y número (regla 2524).");
                }
            }
            else if (line.ReferencedDocumentTypeCode is not null || line.ReferencedSeries is not null || line.ReferencedNumber is not null)
            {
                return Invalid($"{label}: solo las notas llevan documento que modifican (regla 2582).");
            }

            if (string.IsNullOrWhiteSpace(line.Currency) || line.Currency.Length != 3)
            {
                return Invalid($"{label}: moneda inválida.");
            }

            if (new[] { line.TotalAmount, line.TaxedAmount, line.ExemptAmount, line.UnaffectedAmount, line.IgvAmount, line.OtherCharges, line.OtherDiscounts }.Any(a => a < 0))
            {
                return Invalid($"{label}: los importes no pueden ser negativos.");
            }

            if (line.TaxedAmount + line.ExemptAmount + line.UnaffectedAmount <= 0)
            {
                return Unsupported($"{label}: solo se informan operaciones gravadas, exoneradas o inafectas con valor de venta; las gratuitas y exportaciones aún no están soportadas.");
            }

            if (Math.Abs(line.TotalAmount - (line.TaxedAmount + line.ExemptAmount + line.UnaffectedAmount + line.IgvAmount + line.OtherCharges - line.OtherDiscounts)) > TotalTolerance)
            {
                return Unsupported($"{label}: el importe total no coincide con la suma de valores de venta, IGV y otros cargos menos otros descuentos (ISC, ICBPER u otros tributos aún no están soportados).");
            }

            if (line.IgvRate <= 0 || line.IgvRate >= 1)
            {
                return Invalid($"{label}: la tasa de IGV debe expresarse como fracción (por ejemplo 0.18).");
            }

            if ((line.BuyerDocumentTypeCode is null) != (line.BuyerDocumentNumber is null))
            {
                return Invalid($"{label}: el tipo y el número de documento del adquirente se informan juntos o ninguno.");
            }

            if (line.BuyerDocumentTypeCode is null && line.Currency == "PEN" && line.TotalAmount > ReceiptIdentificationThreshold)
            {
                return Invalid($"{label}: una boleta de más de S/ {ReceiptIdentificationThreshold:0} requiere identificar al adquirente (regla 2514).");
            }
        }

        return null;
    }

    private static XElement SignatureInfo(SummaryData data) =>
        new(
            Cac + "Signature",
            new XElement(Cbc + "ID", $"Sign-{data.Ruc}"),
            new XElement(
                Cac + "SignatoryParty",
                new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", data.Ruc)),
                new XElement(Cac + "PartyName", new XElement(Cbc + "Name", data.LegalName))),
            new XElement(
                Cac + "DigitalSignatureAttachment",
                new XElement(Cac + "ExternalReference", new XElement(Cbc + "URI", $"#Sign-{data.Ruc}"))));

    private static XElement Line(SummaryLineData line)
    {
        var element = new XElement(
            Sac + "SummaryDocumentsLine",
            new XElement(Cbc + "LineID", line.LineNumber.ToString(CultureInfo.InvariantCulture)),
            new XElement(Cbc + "DocumentTypeCode", line.DocumentTypeCode),
            new XElement(Cbc + "ID", $"{line.Series}-{line.Number.ToString(CultureInfo.InvariantCulture)}"));

        if (line.BuyerDocumentTypeCode is not null)
        {
            element.Add(new XElement(
                Cac + "AccountingCustomerParty",
                new XElement(Cbc + "CustomerAssignedAccountID", line.BuyerDocumentNumber),
                new XElement(Cbc + "AdditionalAccountID", line.BuyerDocumentTypeCode)));
        }

        if (line.DocumentTypeCode is "07" or "08")
        {
            element.Add(new XElement(
                Cac + "BillingReference",
                new XElement(
                    Cac + "InvoiceDocumentReference",
                    new XElement(Cbc + "ID", $"{line.ReferencedSeries}-{line.ReferencedNumber!.Value.ToString(CultureInfo.InvariantCulture)}"),
                    new XElement(Cbc + "DocumentTypeCode", line.ReferencedDocumentTypeCode))));
        }

        element.Add(
            new XElement(Cac + "Status", new XElement(Cbc + "ConditionCode", line.Status)),
            new XElement(Sac + "TotalAmount", new XAttribute("currencyID", line.Currency), Money(line.TotalAmount)));

        // One BillingPayment per sale-value type that applies (catalogue 11: 01 taxed, 02 exempt, 03 unaffected); the sheet marks them "only if applicable".
        foreach (var (code, amount) in new[] { ("01", line.TaxedAmount), ("02", line.ExemptAmount), ("03", line.UnaffectedAmount) })
        {
            if (amount > 0)
            {
                element.Add(new XElement(
                    Sac + "BillingPayment",
                    new XElement(Cbc + "PaidAmount", new XAttribute("currencyID", line.Currency), Money(amount)),
                    new XElement(Cbc + "InstructionID", code)));
            }
        }

        // Charges that do not affect the base are informed in one node per line (sheet "Sumatoria otros cargos del item"); the discounts only enter the total.
        if (line.OtherCharges > 0)
        {
            element.Add(new XElement(
                Cac + "AllowanceCharge",
                new XElement(Cbc + "ChargeIndicator", "true"),
                new XElement(Cbc + "Amount", new XAttribute("currencyID", line.Currency), Money(line.OtherCharges))));
        }

        // IGV is mandatory in every line (rule 2278), also when it is zero; the rate is mandatory for code 1000 (rule 2992).
        element.Add(new XElement(
            Cac + "TaxTotal",
            new XElement(Cbc + "TaxAmount", new XAttribute("currencyID", line.Currency), Money(line.IgvAmount)),
            new XElement(
                Cac + "TaxSubtotal",
                new XElement(Cbc + "TaxAmount", new XAttribute("currencyID", line.Currency), Money(line.IgvAmount)),
                new XElement(
                    Cac + "TaxCategory",
                    new XElement(Cbc + "Percent", (line.IgvRate * 100m).ToString("0.00", CultureInfo.InvariantCulture)),
                    new XElement(
                        Cac + "TaxScheme",
                        new XElement(Cbc + "ID", "1000"),
                        new XElement(Cbc + "Name", "IGV"),
                        new XElement(Cbc + "TaxTypeCode", "VAT"))))));
        return element;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed class StringWriterUtf8 : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
