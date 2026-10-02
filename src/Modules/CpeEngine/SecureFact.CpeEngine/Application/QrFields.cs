using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.CpeEngine.Application;

/// <summary>What goes into the QR of a document, from what Billing issued.</summary>
internal static class QrFields
{
    /// <summary>
    /// The QR field is "Sumatoria IGV, de ser el caso" (S19 §6.4.3, inciso d): only the IGV. A document taxed with the IVAP, with the IGV of an export or without
    /// tax has no IGV and states 0.00; the IVAP is another tax (1016) that the annex does not name, so it never takes the IGV slot (R-018).
    /// </summary>
    public static QrData From(DocumentDto d, string issuerRuc, string digestValue)
    {
        var identified = d.Buyer.DocumentTypeCode != IdentityDocuments.NoDocument;
        return new QrData(
            issuerRuc, d.DocumentTypeCode, d.Series, d.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Totals.TotalIgv, d.Totals.PayableAmount,
            d.IssueDate, identified ? d.Buyer.DocumentTypeCode : null, identified ? d.Buyer.DocumentNumber : null, digestValue);
    }
}
