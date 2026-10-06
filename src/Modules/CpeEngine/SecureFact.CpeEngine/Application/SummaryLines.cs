using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel.Domain;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.CpeEngine.Application;

/// <summary>Builds the summary line of a receipt, or of a note of a receipt, from the document Billing issued: the amounts are Billing's, never recomputed.</summary>
internal static class SummaryLines
{
    /// <param name="status">1 reports the document, 3 voids it (catalogue 19).</param>
    public static SummaryLineData From(DocumentDto d, int lineNumber, decimal igvRate, decimal ivapRate, string status = "1")
    {
        var identified = d.Buyer.DocumentTypeCode != IdentityDocuments.NoDocument;

        // A document taxed with the IVAP states that tax (1016) with its own base and rate; the engine does not mix it with the IGV.
        var ivap = d.Totals.TotalIvap > 0;
        var taxed = ivap ? d.Totals.TaxSubtotals.Where(t => t.TaxCode == TaxCodes.Ivap).Sum(t => t.TaxableAmount) : d.Totals.TotalTaxableGravado;
        return new SummaryLineData(
            lineNumber, d.Series, d.Number, identified ? d.Buyer.DocumentTypeCode : null, identified ? d.Buyer.DocumentNumber : null, d.Currency,
            d.Totals.PayableAmount, taxed, d.Totals.TotalExempt, d.Totals.TotalUnaffected, ivap ? d.Totals.TotalIvap : d.Totals.TotalIgv, ivap ? ivapRate : igvRate,
            d.DocumentTypeCode, d.Note?.ReferencedDocumentTypeCode, d.Note?.ReferencedSeries, d.Note?.ReferencedNumber, status,
            d.Totals.TotalCharges, d.Totals.TotalAllowances, ivap, d.Totals.TotalExport, d.Totals.TotalIsc, d.Totals.TotalIcbper);
    }
}
