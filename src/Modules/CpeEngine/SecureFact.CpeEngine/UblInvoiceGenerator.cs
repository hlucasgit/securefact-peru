using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.CpeEngine;

/// <summary>
/// UBL 2.1 Invoice for factura (01) and boleta (03). Structure follows the mandatory/conditional tags of the 2026-08-26 validation
/// rules workbook (sheets Factura2_0/Boleta2_0, S16) and the XML guides (S18). Scope today: taxed (10), exempt (20), unaffected (30) and
/// free (11–16, 21, 31–37) lines with line discounts/charges (catalogue 53: 00, 01, 47, 48) and global ones (02, 03, 49, 50) in invoices and receipts, but not in notes;
/// everything else returns <c>SF-CPE-002</c> instead of
/// producing XML that would not match the calculated amounts. The output is unsigned: the signer fills <c>ext:ExtensionContent</c>.
/// </summary>
internal sealed class UblInvoiceGenerator : IUblDocumentGenerator
{
    private static readonly XNamespace Inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace CreditNoteNs = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    private static readonly XNamespace DebitNoteNs = "urn:oasis:names:specification:ubl:schema:xsd:DebitNote-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    /// <summary>Tax category letter (UN/ECE 5305, guide S18), scheme name and international code (catalogue 05) per tax code.</summary>
    private static readonly Dictionary<string, (string Letter, string Name, string TypeCode)> Schemes = new(StringComparer.Ordinal)
    {
        [TaxCodes.Igv] = ("S", "IGV", "VAT"),
        [TaxCodes.Ivap] = ("S", "IVAP", "VAT"),
        [TaxCodes.Export] = ("G", "EXP", "FRE"),
        [TaxCodes.Exempt] = ("E", "EXO", "VAT"),
        [TaxCodes.Unaffected] = ("O", "INA", "FRE"),
        [TaxCodes.Free] = ("Z", "GRA", "FRE"),
    };

    internal static IReadOnlyDictionary<string, (string Letter, string Name, string TypeCode)> SupportedSchemes => Schemes;

    public Result<UblDocument> GenerateInvoice(UblInvoiceData data)
    {
        if (Validate(data) is { } invalid)
        {
            return invalid;
        }

        var currency = data.Currency;
        var totals = data.Totals;
        var root = new XElement(
            Inv + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ext", Ext),
            new XAttribute(XNamespace.Xmlns + "ds", Ds),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.1"),
            new XElement(Cbc + "CustomizationID", "2.0"),
            new XElement(Cbc + "ID", $"{data.Series}-{data.Number.ToString(CultureInfo.InvariantCulture)}"),
            new XElement(Cbc + "IssueDate", data.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        if (data.IssueTime is { } time)
        {
            root.Add(new XElement(Cbc + "IssueTime", time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        root.Add(
            new XElement(
                Cbc + "InvoiceTypeCode",
                new XAttribute("listID", data.OperationTypeCode),
                new XAttribute("listAgencyName", "PE:SUNAT"),
                new XAttribute("listName", "Tipo de Documento"),
                new XAttribute("listURI", "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo01"),
                data.DocumentTypeCode),
            Legends(totals),
            new XElement(Cbc + "DocumentCurrencyCode", currency),
            new XElement(Cbc + "LineCountNumeric", data.Lines.Count.ToString(CultureInfo.InvariantCulture)),
            SignatureInfo(data.Issuer),
            Supplier(data.Issuer),
            Customer(data.Buyer));

        // Invoices must state their payment form (error 3244 since 2022-01-01, found against the SUNAT beta service). Receipts do not carry it.
        if (data.DocumentTypeCode == "01")
        {
            root.Add(PaymentTerms(data.PaymentForm, data.Installments, currency));
        }

        foreach (var allowance in GlobalAllowances(data))
        {
            root.Add(AllowanceElement(allowance, currency));
        }

        root.Add(
            TaxTotal(totals, data.IgvRate, currency),
            MonetaryTotal(totals, currency));

        for (var i = 0; i < data.Lines.Count; i++)
        {
            root.Add(Line(data.Lines[i], totals.Lines[i], data.IgvRate, data.IvapRate, currency));
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var buffer = new StringWriterUtf8();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }

        var fileBase = $"{data.Issuer.DocumentNumber}-{data.DocumentTypeCode}-{data.Series}-{data.Number.ToString(CultureInfo.InvariantCulture)}";
        return new UblDocument(buffer.ToString(), fileBase);
    }

    /// <summary>Reasons whose structure is supported today: credit 01-10 and debit 01-03. Export (11), IVAP (12) and credit-installment (13) adjustments are not.</summary>
    private static readonly HashSet<string> CreditReasons = ["01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12", "13"];

    /// <summary>Catalogue 51 types the generator supports: the internal sale and the export of goods.</summary>
    private const string SaleOperation = "0101";

    private const string ExportOperation = "0200";

    /// <summary>Reason of a credit note that adjusts export operations (catalogue 09, code 11).</summary>
    private const string ExportAdjustmentReason = "11";

    /// <summary>
    /// The operation type decides the nature of the lines (sheet Factura2_0, rules 2642, 3107, 3223): an export (0200, goods) is an invoice whose lines are all export lines
    /// (tax 9995); the internal sale carries none.
    /// </summary>
    private static Error? CheckOperationType(UblInvoiceData data)
    {
        var exportLines = data.Totals.Lines.Count(l => l.TaxCode == TaxCodes.Export);
        switch (data.OperationTypeCode)
        {
            case SaleOperation:
                return exportLines == 0 ? null : Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de documento inválidos", "Las líneas de exportación requieren el tipo de operación 0200.");
            case ExportOperation:
                if (data.DocumentTypeCode != "01")
                {
                    return Error.Validation(ErrorCodes.CpeUnsupported, "Documento no soportado por el generador", "La exportación solo se emite con facturas.");
                }

                return exportLines == data.Totals.Lines.Count
                    ? null
                    : Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de documento inválidos", "Una exportación lleva solo líneas de exportación (afectación 40).");
            default:
                return Error.Validation(ErrorCodes.CpeUnsupported, "Documento no soportado por el generador", $"El tipo de operación '{data.OperationTypeCode}' no está soportado (0101 o 0200).");
        }
    }

    /// <summary>A credit note of reason 11 (adjustment of an export) carries only export lines and modifies an invoice (rules 2642, 3194, 3221, 3107).</summary>
    private static Error? CheckNoteExport(UblNoteData data)
    {
        var exportLines = data.Totals.Lines.Count(l => l.TaxCode == TaxCodes.Export);
        if (data.ReasonCode == ExportAdjustmentReason)
        {
            return exportLines == data.Totals.Lines.Count && data.ReferencedDocumentTypeCode == "01"
                ? null
                : Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de nota inválidos", "La nota de motivo 11 (ajuste de exportación) lleva solo líneas de exportación y modifica una factura.");
        }

        return exportLines == 0 || exportLines == data.Totals.Lines.Count
            ? null
            : Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de nota inválidos", "Una nota no mezcla líneas de exportación con otras.");
    }

    /// <summary>Reason of a credit note that adjusts operations taxed with the IVAP (catalogue 09, code 12).</summary>
    private const string IvapAdjustmentReason = "12";

    /// <summary>
    /// IVAP lines (affectation 17, tax 1016) need the IVAP rate to state it (rule 3103). A credit note of reason 12 carries only IVAP lines (rules 2644, 3221, 3107) and
    /// IVAP lines are reserved to it among notes (rule 3230); invoices and receipts may carry them freely.
    /// </summary>
    private static Error? CheckIvap(TaxCalculationResult totals, decimal ivapRate, bool adjustment, bool isNote)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de documento inválidos", detail);

        var ivapLines = totals.Lines.Count(l => l.TaxCode == TaxCodes.Ivap);
        if (ivapLines > 0 && ivapRate is <= 0 or >= 1)
        {
            return Invalid("Las líneas con IVAP requieren la tasa del IVAP, expresada como fracción (por ejemplo 0.04).");
        }

        if (adjustment && ivapLines != totals.Lines.Count)
        {
            return Invalid("La nota de crédito de motivo 12 (ajuste IVAP) lleva solo líneas afectas al IVAP.");
        }

        if (isNote && !adjustment && ivapLines > 0)
        {
            return Invalid("Solo la nota de crédito de motivo 12 lleva líneas afectas al IVAP.");
        }

        return null;
    }

    /// <summary>Reason of a credit note that adjusts the installments of a credit invoice (catalogue 09, code 13).</summary>
    private const string InstallmentAdjustmentReason = "13";

    /// <summary>
    /// The payment terms: one <c>FormaPago</c> term with the form (and, on credit, the net pending amount) and one more per installment (Cuota001, Cuota002, ...)
    /// with its amount and due date (rules 3245-3256).
    /// </summary>
    private static List<XElement> PaymentTerms(string form, IReadOnlyList<UblInstallment>? installments, string currency)
    {
        var terms = new List<XElement>
        {
            new(Cac + "PaymentTerms", new XElement(Cbc + "ID", "FormaPago"), new XElement(Cbc + "PaymentMeansID", form), form == "Credito" ? Amount("Amount", installments!.Sum(i => i.Amount), currency) : null),
        };
        for (var i = 0; form == "Credito" && i < installments!.Count; i++)
        {
            terms.Add(new XElement(
                Cac + "PaymentTerms",
                new XElement(Cbc + "ID", "FormaPago"),
                new XElement(Cbc + "PaymentMeansID", $"Cuota{(i + 1).ToString("D3", CultureInfo.InvariantCulture)}"),
                Amount("Amount", installments[i].Amount, currency),
                new XElement(Cbc + "PaymentDueDate", installments[i].DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }

        return terms;
    }

    private static readonly HashSet<string> DebitReasons = ["01", "02", "03"];

    public Result<UblDocument> GenerateNote(UblNoteData data)
    {
        if (ValidateNote(data) is { } invalid)
        {
            return invalid;
        }

        var credit = data.DocumentTypeCode == "07";
        var ns = credit ? CreditNoteNs : DebitNoteNs;
        var currency = data.Currency;
        var totals = data.Totals;
        var reference = $"{data.ReferencedSeries}-{data.ReferencedNumber.ToString(CultureInfo.InvariantCulture)}";
        var root = new XElement(
            ns + (credit ? "CreditNote" : "DebitNote"),
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ext", Ext),
            new XAttribute(XNamespace.Xmlns + "ds", Ds),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.1"),
            new XElement(Cbc + "CustomizationID", "2.0"),
            new XElement(Cbc + "ID", $"{data.Series}-{data.Number.ToString(CultureInfo.InvariantCulture)}"),
            new XElement(Cbc + "IssueDate", data.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        if (data.IssueTime is { } time)
        {
            root.Add(new XElement(Cbc + "IssueTime", time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        root.Add(
            Legends(totals),
            new XElement(Cbc + "DocumentCurrencyCode", currency),
            new XElement(Cbc + "LineCountNumeric", data.Lines.Count.ToString(CultureInfo.InvariantCulture)),
            new XElement(
                Cac + "DiscrepancyResponse",
                new XElement(Cbc + "ReferenceID", reference),
                new XElement(
                    Cbc + "ResponseCode",
                    new XAttribute("listAgencyName", "PE:SUNAT"),
                    new XAttribute("listName", credit ? "Tipo de nota de credito" : "Tipo de nota de debito"),
                    new XAttribute("listURI", credit ? "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo09" : "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo10"),
                    data.ReasonCode),
                new XElement(Cbc + "Description", data.ReasonDescription)),
            new XElement(
                Cac + "BillingReference",
                new XElement(
                    Cac + "InvoiceDocumentReference",
                    new XElement(Cbc + "ID", reference),
                    new XElement(
                        Cbc + "DocumentTypeCode",
                        new XAttribute("listAgencyName", "PE:SUNAT"),
                        new XAttribute("listName", "Tipo de Documento"),
                        new XAttribute("listURI", "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo01"),
                        data.ReferencedDocumentTypeCode))),
            SignatureInfo(data.Issuer),
            Supplier(data.Issuer),
            Customer(data.Buyer),
            data.ReasonCode == InstallmentAdjustmentReason ? PaymentTerms("Credito", data.Installments, currency) : null,
            TaxTotal(totals, data.IgvRate, currency),
            new XElement(
                Cac + (credit ? "LegalMonetaryTotal" : "RequestedMonetaryTotal"),
                Amount("LineExtensionAmount", totals.TotalLineExtensionAmount, currency),
                Amount("TaxInclusiveAmount", totals.TotalTaxInclusiveAmount, currency),
                Amount("PayableAmount", totals.PayableAmount, currency)));

        for (var i = 0; i < data.Lines.Count; i++)
        {
            root.Add(Line(data.Lines[i], totals.Lines[i], data.IgvRate, data.IvapRate, currency, credit ? "CreditNoteLine" : "DebitNoteLine", credit ? "CreditedQuantity" : "DebitedQuantity"));
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var buffer = new StringWriterUtf8();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }

        var fileBase = $"{data.Issuer.DocumentNumber}-{data.DocumentTypeCode}-{data.Series}-{data.Number.ToString(CultureInfo.InvariantCulture)}";
        return new UblDocument(buffer.ToString(), fileBase);
    }

    /// <summary>
    /// Reason 13 modifies an invoice (rule 3259) and states the new installments (3257, 3249); its payable amount is zero (3315). No other reason carries installments.
    /// </summary>
    private static Error? CheckNoteInstallments(UblNoteData data)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de nota inválidos", detail);

        var installments = data.Installments ?? [];
        if (data.ReasonCode != InstallmentAdjustmentReason)
        {
            return installments.Count == 0 ? null : Invalid("Solo la nota de crédito de motivo 13 lleva cuotas.");
        }

        if (data.DocumentTypeCode != "07" || data.ReferencedDocumentTypeCode != "01")
        {
            return Invalid("El motivo 13 (ajuste de cuotas) es de una nota de crédito sobre una factura.");
        }

        if (installments.Count is 0 or > MaxInstallments || installments.Any(i => i.Amount <= 0))
        {
            return Invalid($"El motivo 13 lleva de 1 a {MaxInstallments} cuotas con monto mayor que cero.");
        }

        return data.Totals.PayableAmount == 0 ? null : Invalid("El importe total de una nota de motivo 13 debe ser cero.");
    }

    private static Error? ValidateNote(UblNoteData data)
    {
        static Error Unsupported(string detail) => Error.Validation(ErrorCodes.CpeUnsupported, "Nota no soportada por el generador", detail);
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de nota inválidos", detail);

        if (data is null || data.Lines is not { Count: > 0 } || data.Totals is null || data.Totals.Lines.Count != data.Lines.Count)
        {
            return Invalid("La nota requiere líneas y totales calculados que coincidan línea a línea.");
        }

        if (data.DocumentTypeCode is not ("07" or "08"))
        {
            return Invalid($"El tipo de documento '{data.DocumentTypeCode}' no es una nota de crédito (07) ni de débito (08).");
        }

        if (!(data.DocumentTypeCode == "07" ? CreditReasons : DebitReasons).Contains(data.ReasonCode ?? string.Empty))
        {
            return Unsupported($"El código de motivo '{data.ReasonCode}' no está soportado para este tipo de nota (crédito 01-10, débito 01-03).");
        }

        if (string.IsNullOrWhiteSpace(data.ReasonDescription) || data.ReasonDescription.Length > 500 || data.ReasonDescription.Any(c => c is '\n' or '\r' or '\t'))
        {
            return Invalid("El sustento es obligatorio (1 a 500 caracteres, sin saltos de línea ni tabulaciones).");
        }

        if (data.ReferencedDocumentTypeCode is not ("01" or "03") || string.IsNullOrWhiteSpace(data.ReferencedSeries) || data.ReferencedNumber < 1)
        {
            return Invalid("La nota debe modificar una factura (01) o boleta (03) identificada por serie y número.");
        }

        if (CheckNoteInstallments(data) is { } badInstallments)
        {
            return badInstallments;
        }

        if (data.Totals.TotalIsc != 0 || data.Totals.TotalIcbper != 0)
        {
            return Unsupported("ISC e ICBPER aún no están soportados por el generador UBL.");
        }

        if (CheckNoteExport(data) is { } badExport)
        {
            return badExport;
        }

        if (CheckIvap(data.Totals, data.IvapRate, data.ReasonCode == IvapAdjustmentReason, isNote: true) is { } badIvap)
        {
            return badIvap;
        }

        if (data.Totals.TotalAllowances != 0 || data.Totals.TotalCharges != 0 || data.Totals.PayableRoundingAmount != 0 || data.Lines.Any(HasLineAdjustments))
        {
            return Unsupported("Cargos, descuentos y redondeo aún no están soportados por el generador UBL para notas.");
        }

        for (var i = 0; i < data.Lines.Count; i++)
        {
            if (!Schemes.ContainsKey(data.Totals.Lines[i].TaxCode))
            {
                return Unsupported($"La línea {i + 1} usa el tributo {data.Totals.Lines[i].TaxCode}, no soportado todavía.");
            }
        }

        return string.IsNullOrWhiteSpace(data.Series) || data.Number < 1 ? Invalid("Serie y número son obligatorios.") : null;
    }

    private static Error? Validate(UblInvoiceData data)
    {
        static Error Unsupported(string detail) => Error.Validation(ErrorCodes.CpeUnsupported, "Documento no soportado por el generador", detail);
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de documento inválidos", detail);

        if (data is null || data.Lines is not { Count: > 0 } || data.Totals is null || data.Totals.Lines.Count != data.Lines.Count)
        {
            return Invalid("El documento requiere líneas y totales calculados que coincidan línea a línea.");
        }

        if (data.DocumentTypeCode is not ("01" or "03"))
        {
            return Unsupported($"El tipo de documento '{data.DocumentTypeCode}' no está soportado (solo 01 y 03).");
        }

        if (data.Totals.TotalIsc != 0 || data.Totals.TotalIcbper != 0)
        {
            return Unsupported("ISC e ICBPER aún no están soportados por el generador UBL.");
        }

        if (CheckOperationType(data) is { } badOperation)
        {
            return badOperation;
        }

        if (CheckIvap(data.Totals, data.IvapRate, adjustment: false, isNote: false) is { } badIvap)
        {
            return badIvap;
        }

        if (data.Totals.PayableRoundingAmount != 0)
        {
            return Unsupported("El redondeo del importe total aún no está soportado por el generador UBL.");
        }

        if (CheckAdjustments(data) is { } inconsistent)
        {
            return inconsistent;
        }

        for (var i = 0; i < data.Lines.Count; i++)
        {
            if (!Schemes.ContainsKey(data.Totals.Lines[i].TaxCode))
            {
                return Unsupported($"La línea {i + 1} usa el tributo {data.Totals.Lines[i].TaxCode}, no soportado todavía.");
            }
        }

        if (CheckPayment(data) is { } badPayment)
        {
            return badPayment;
        }

        if (string.IsNullOrWhiteSpace(data.Series) || data.Number < 1 || string.IsNullOrWhiteSpace(data.OperationTypeCode))
        {
            return Invalid("Serie, número y tipo de operación son obligatorios.");
        }

        return null;
    }

    /// <summary>Highest installment number the identifier <c>Cuota[0-9]{3}</c> can carry (rule 3246).</summary>
    private const int MaxInstallments = 999;

    /// <summary>
    /// The payment form and its installments agree: cash carries none; credit carries one to 999 whose amounts add up to the payable amount (rules 3265, 3266, 3319)
    /// and fall due after the issue date (3267); only an invoice has a payment form (the receipt sheet defines none).
    /// </summary>
    private static Error? CheckPayment(UblInvoiceData data)
    {
        static Error Unsupported(string detail) => Error.Validation(ErrorCodes.CpeUnsupported, "Documento no soportado por el generador", detail);
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de documento inválidos", detail);

        var installments = data.Installments ?? [];
        switch (data.PaymentForm)
        {
            case "Contado":
                return installments.Count == 0 && data.InitialPayment == 0 ? null : Invalid("Una venta al contado no lleva cuotas ni entrega inicial.");
            case "Credito":
                if (data.DocumentTypeCode != "01")
                {
                    return Unsupported("Solo las facturas tienen forma de pago al crédito.");
                }

                if (installments.Count is 0 or > MaxInstallments)
                {
                    return Invalid($"Una venta al crédito lleva de 1 a {MaxInstallments} cuotas.");
                }

                if (installments.Any(i => i.Amount <= 0 || i.DueDate <= data.IssueDate))
                {
                    return Invalid("Cada cuota debe tener un monto mayor que cero y vencer después de la fecha de emisión.");
                }

                // The net pending amount (the sum of the installments) is the payable amount minus what was paid on the issue date (rules 3265, 3319).
                if (data.InitialPayment < 0 || data.InitialPayment >= data.Totals.PayableAmount)
                {
                    return Invalid("La entrega inicial debe ser positiva y menor que el importe total.");
                }

                return installments.Sum(i => i.Amount) == data.Totals.PayableAmount - data.InitialPayment
                    ? null
                    : Invalid("Las cuotas deben sumar el importe total de la venta menos la entrega inicial.");
            default:
                return Unsupported($"La forma de pago '{data.PaymentForm}' no está soportada (Contado o Credito).");
        }
    }

    /// <summary>One cac:AllowanceCharge: a discount (<c>ChargeIndicator</c> false) or a charge (true) of catalogue 53, with the base it applies to when the structure asks for it.</summary>
    private readonly record struct Allowance(string Code, bool IsCharge, decimal Amount, decimal? BaseAmount);

    private static bool HasLineAdjustments(UblLine line) =>
        line.DiscountAffectingBase != 0 || line.ChargeAffectingBase != 0 || line.DiscountNotAffectingBase != 0 || line.ChargeNotAffectingBase != 0;

    /// <summary>
    /// Line discounts/charges (sheet Factura2_0, rows "Cargo/descuento por ítem"). Codes 00 and 47 apply to the value of the line before them; 01 and 48 do not change
    /// the base and are stated over the line value. The sheet mandates amount, base and factor at line level, the factor being checked as amount = base × factor (rule 3290).
    /// </summary>
    private static List<Allowance> LineAllowances(UblLine line, LineTaxResult result)
    {
        var gross = result.LineExtensionAmount + line.DiscountAffectingBase - line.ChargeAffectingBase;
        var list = new List<Allowance>();
        AddIfAny(list, "00", false, line.DiscountAffectingBase, gross);
        AddIfAny(list, "47", true, line.ChargeAffectingBase, gross);
        AddIfAny(list, "01", false, line.DiscountNotAffectingBase, result.LineExtensionAmount);
        AddIfAny(list, "48", true, line.ChargeNotAffectingBase, result.LineExtensionAmount);
        return list;
    }

    /// <summary>
    /// Global discounts/charges: 02 and 49 change the taxable base of the IGV (rules 3277, 3278, 3291) and are stated over the taxed base before them;
    /// 03 and 50 do not and enter the allowance/charge totals (rules 3300, 3301). The base and the factor are optional at this level, so only the first pair carries them.
    /// </summary>
    private static List<Allowance> GlobalAllowances(UblInvoiceData data)
    {
        var adjustments = data.Adjustments ?? new GlobalAdjustments();
        var taxedBefore = data.Totals.TotalTaxableGravado + adjustments.DiscountAffectingBase - adjustments.ChargeAffectingBase;
        var list = new List<Allowance>();
        AddIfAny(list, "02", false, adjustments.DiscountAffectingBase, taxedBefore);
        AddIfAny(list, "49", true, adjustments.ChargeAffectingBase, taxedBefore);
        AddIfAny(list, "03", false, adjustments.DiscountNotAffectingBase, null);
        AddIfAny(list, "50", true, adjustments.ChargeNotAffectingBase, null);
        return list;
    }

    private static void AddIfAny(List<Allowance> list, string code, bool isCharge, decimal amount, decimal? baseAmount)
    {
        if (amount != 0)
        {
            list.Add(new Allowance(code, isCharge, amount, baseAmount));
        }
    }

    /// <summary>Factor n(3,5): amount ÷ base to 5 decimals; null when the base is not positive or the factor does not fit the format (positive, below 1000).</summary>
    private static decimal? Factor(Allowance allowance)
    {
        if (allowance.BaseAmount is not { } baseAmount)
        {
            return null;
        }

        if (baseAmount <= 0)
        {
            return -1m;
        }

        var factor = Math.Round(allowance.Amount / baseAmount, 5, MidpointRounding.AwayFromZero);
        return factor is > 0m and < 1000m ? factor : -1m;
    }

    private static Error? CheckAdjustments(UblInvoiceData data)
    {
        var adjustments = data.Adjustments ?? new GlobalAdjustments();
        var allowances = data.Lines.Sum(l => l.DiscountNotAffectingBase) + adjustments.DiscountNotAffectingBase;
        var charges = data.Lines.Sum(l => l.ChargeNotAffectingBase) + adjustments.ChargeNotAffectingBase;
        if (data.Totals.TotalAllowances != allowances || data.Totals.TotalCharges != charges)
        {
            return Error.Validation(ErrorCodes.CpeInvalidDocument, "Datos de documento inválidos", "Los descuentos y cargos informados no coinciden con los totales calculados.");
        }

        for (var i = 0; i < data.Lines.Count; i++)
        {
            foreach (var allowance in LineAllowances(data.Lines[i], data.Totals.Lines[i]))
            {
                if (Factor(allowance) == -1m)
                {
                    return Error.Validation(ErrorCodes.CpeUnsupported, "Documento no soportado por el generador", $"La línea {i + 1} tiene un descuento o cargo de código {allowance.Code} cuya base y factor no son válidos (la base debe ser mayor que cero).");
                }
            }
        }

        foreach (var allowance in GlobalAllowances(data))
        {
            if (Factor(allowance) == -1m)
            {
                return Error.Validation(ErrorCodes.CpeUnsupported, "Documento no soportado por el generador", $"El descuento o cargo global de código {allowance.Code} no tiene una base y un factor válidos (la base gravada debe ser mayor que cero).");
            }
        }

        return null;
    }

    private static XElement AllowanceElement(Allowance allowance, string currency)
    {
        var element = new XElement(
            Cac + "AllowanceCharge",
            new XElement(Cbc + "ChargeIndicator", allowance.IsCharge ? "true" : "false"),
            new XElement(
                Cbc + "AllowanceChargeReasonCode",
                new XAttribute("listAgencyName", "PE:SUNAT"),
                new XAttribute("listName", "Cargo/descuento"),
                new XAttribute("listURI", "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo53"),
                allowance.Code));
        if (Factor(allowance) is { } factor)
        {
            element.Add(new XElement(Cbc + "MultiplierFactorNumeric", factor.ToString("0.00###", CultureInfo.InvariantCulture)));
        }

        element.Add(Amount("Amount", allowance.Amount, currency));
        if (allowance.BaseAmount is { } baseAmount)
        {
            element.Add(Amount("BaseAmount", baseAmount, currency));
        }

        return element;
    }

    /// <summary>Legend 2007 "Operación sujeta a IVAP" when a line is taxed with the IVAP (observation 4264 otherwise).</summary>
    private static List<XElement> Legends(TaxCalculationResult totals) =>
        totals.Lines.Any(l => l.TaxCode == TaxCodes.Ivap && l.LineExtensionAmount > 0)
            ? [new XElement(Cbc + "Note", new XAttribute("languageLocaleID", "2007"), "Operación sujeta a IVAP")]
            : [];

    private static XElement SignatureInfo(UblParty issuer) =>
        new(
            Cac + "Signature",
            new XElement(Cbc + "ID", "IDSignSP"),
            new XElement(
                Cac + "SignatoryParty",
                new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", issuer.DocumentNumber)),
                new XElement(Cac + "PartyName", new XElement(Cbc + "Name", issuer.LegalName))),
            new XElement(
                Cac + "DigitalSignatureAttachment",
                new XElement(Cac + "ExternalReference", new XElement(Cbc + "URI", "#SignatureSP"))));

    private static XElement Supplier(UblParty issuer)
    {
        var party = new XElement(
            Cac + "Party",
            new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", IdentityScheme(issuer.DocumentTypeCode), issuer.DocumentNumber)));
        if (!string.IsNullOrWhiteSpace(issuer.TradeName))
        {
            party.Add(new XElement(Cac + "PartyName", new XElement(Cbc + "Name", issuer.TradeName)));
        }

        party.Add(new XElement(
            Cac + "PartyLegalEntity",
            new XElement(Cbc + "RegistrationName", issuer.LegalName),
            new XElement(Cac + "RegistrationAddress", new XElement(Cbc + "AddressTypeCode", issuer.EstablishmentCode ?? "0000"))));
        return new XElement(Cac + "AccountingSupplierParty", party);
    }

    private static XElement Customer(UblParty buyer) =>
        new(
            Cac + "AccountingCustomerParty",
            new XElement(
                Cac + "Party",
                new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", IdentityScheme(buyer.DocumentTypeCode), buyer.DocumentNumber)),
                new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", buyer.LegalName))));

    private static XElement TaxTotal(TaxCalculationResult totals, decimal igvRate, string currency)
    {
        var element = new XElement(Cac + "TaxTotal", Amount("TaxAmount", totals.TotalTaxAmount, currency));
        foreach (var subtotal in totals.TaxSubtotals)
        {
            element.Add(new XElement(
                Cac + "TaxSubtotal",
                Amount("TaxableAmount", subtotal.TaxableAmount, currency),
                Amount("TaxAmount", subtotal.TaxAmount, currency),
                Category(subtotal.TaxCode, igvRate, null)));
        }

        return element;
    }

    /// <param name="igvRate">The rate to state when the category carries one: the IGV rate for IGV and informational-IGV lines, the IVAP rate for IVAP lines.</param>
    private static XElement Category(string taxCode, decimal igvRate, string? exemptionReasonCode)
    {
        var (letter, name, typeCode) = Schemes[taxCode];
        var category = new XElement(
            Cac + "TaxCategory",
            new XElement(
                Cbc + "ID",
                new XAttribute("schemeID", "UN/ECE 5305"),
                new XAttribute("schemeName", "Tax Category Identifier"),
                new XAttribute("schemeAgencyName", "United Nations Economic Commission for Europe"),
                letter));

        if (exemptionReasonCode is not null)
        {
            // Every line category states its rate (rule 2992, found against the SUNAT beta on an exempt line): the IGV rate for taxed lines and for free ones that
            // report an informational IGV (11-16; rule 2993 forbids 0 there), and 0.00 for the rest.
            var carriesRate = taxCode is TaxCodes.Igv or TaxCodes.Ivap || (taxCode == TaxCodes.Free && exemptionReasonCode is "11" or "12" or "13" or "14" or "15" or "16");
            category.Add(new XElement(Cbc + "Percent", (carriesRate ? igvRate * 100m : 0m).ToString("0.00", CultureInfo.InvariantCulture)));

            category.Add(new XElement(
                Cbc + "TaxExemptionReasonCode",
                new XAttribute("listAgencyName", "PE:SUNAT"),
                new XAttribute("listName", "Afectacion del IGV"),
                new XAttribute("listURI", "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo07"),
                exemptionReasonCode));
        }

        category.Add(new XElement(
            Cac + "TaxScheme",
            new XElement(
                Cbc + "ID",
                new XAttribute("schemeID", "UN/ECE 5153"),
                new XAttribute("schemeName", "Codigo de tributos"),
                new XAttribute("schemeAgencyName", "PE:SUNAT"),
                new XAttribute("schemeURI", "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo05"),
                taxCode),
            new XElement(Cbc + "Name", name),
            new XElement(Cbc + "TaxTypeCode", typeCode)));
        return category;
    }

    private static XElement MonetaryTotal(TaxCalculationResult totals, string currency) =>
        new(
            Cac + "LegalMonetaryTotal",
            Amount("LineExtensionAmount", totals.TotalLineExtensionAmount, currency),
            Amount("TaxInclusiveAmount", totals.TotalTaxInclusiveAmount, currency),
            totals.TotalAllowances > 0 ? Amount("AllowanceTotalAmount", totals.TotalAllowances, currency) : null,
            totals.TotalCharges > 0 ? Amount("ChargeTotalAmount", totals.TotalCharges, currency) : null,
            Amount("PayableAmount", totals.PayableAmount, currency));

    private static XElement Line(UblLine line, LineTaxResult result, decimal igvRate, decimal ivapRate, string currency, string lineName = "InvoiceLine", string quantityName = "InvoicedQuantity")
    {
        var rate = result.TaxCode == TaxCodes.Ivap ? ivapRate : igvRate;
        var isFree = result.TaxCode == TaxCodes.Free;
        var element = new XElement(
            Cac + lineName,
            new XElement(Cbc + "ID", line.LineNumber.ToString(CultureInfo.InvariantCulture)),
            new XElement(
                Cbc + quantityName,
                new XAttribute("unitCode", line.UnitCode),
                new XAttribute("unitCodeListID", "UN/ECE rec 20"),
                new XAttribute("unitCodeListAgencyName", "United Nations Economic Commission for Europe"),
                Decimal(line.Quantity)),
            Amount("LineExtensionAmount", result.LineExtensionAmount, currency));

        var referencePrice = isFree ? line.ReferenceUnitValue ?? 0m : result.UnitPriceIncludingTaxes ?? 0m;
        element.Add(new XElement(
            Cac + "PricingReference",
            new XElement(
                Cac + "AlternativeConditionPrice",
                new XElement(Cbc + "PriceAmount", new XAttribute("currencyID", currency), Decimal(referencePrice)),
                new XElement(Cbc + "PriceTypeCode", isFree ? "02" : "01"))));

        foreach (var allowance in LineAllowances(line, result))
        {
            element.Add(AllowanceElement(allowance, currency));
        }

        // Free operations report the informational IGV (11–16) under tax 9996 but the line total tax stays 0 (S16, rule 3302).
        var lineTax = isFree ? result.IgvOrIvapAmount : result.TotalTaxAmount;
        element.Add(new XElement(
            Cac + "TaxTotal",
            Amount("TaxAmount", lineTax, currency),
            new XElement(
                Cac + "TaxSubtotal",
                Amount("TaxableAmount", result.LineExtensionAmount, currency),
                Amount("TaxAmount", lineTax, currency),
                Category(result.TaxCode, rate, line.IgvAffectationCode))));

        var item = new XElement(Cac + "Item", new XElement(Cbc + "Description", line.Description));
        if (!string.IsNullOrWhiteSpace(line.ProductCode))
        {
            item.Add(new XElement(Cac + "SellersItemIdentification", new XElement(Cbc + "ID", line.ProductCode)));
        }

        element.Add(item);
        element.Add(new XElement(Cac + "Price", new XElement(Cbc + "PriceAmount", new XAttribute("currencyID", currency), Decimal(isFree ? 0m : line.UnitValue))));
        return element;
    }

    /// <summary>Attributes of a catalogue 06 identity number, with the values the validation rules expect (observations 4255–4257 otherwise).</summary>
    private static XAttribute[] IdentityScheme(string typeCode) =>
    [
        new("schemeID", typeCode),
        new("schemeName", "Documento de Identidad"),
        new("schemeAgencyName", "PE:SUNAT"),
        new("schemeURI", "urn:pe:gob:sunat:cpe:see:gem:catalogos:catalogo06"),
    ];

    private static XElement Amount(string name, decimal value, string currency) =>
        new(Cbc + name, new XAttribute("currencyID", currency), value.ToString("0.00", CultureInfo.InvariantCulture));

    /// <summary>Quantities and unit values keep up to 10 decimals, at least two (n(12,10)).</summary>
    private static string Decimal(decimal value) => value.ToString("0.00##########", CultureInfo.InvariantCulture);

    private sealed class StringWriterUtf8 : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
