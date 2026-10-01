using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>Values printed in the QR of a printed representation, exactly as they appear in the electronic document.</summary>
/// <param name="IssuerRuc">RUC of the issuer (11 digits).</param>
/// <param name="DocumentTypeCode">Catalogue 01 code, e.g. 01 invoice, 03 receipt.</param>
/// <param name="Series">Series, e.g. F001.</param>
/// <param name="Number">Correlative number as written in the document.</param>
/// <param name="TotalIgv">Sum of IGV; 0 when the document has no IGV.</param>
/// <param name="TotalAmount">Importe total de la venta, cesión en uso o servicio prestado.</param>
/// <param name="IssueDate">Fecha de emisión.</param>
/// <param name="BuyerDocumentTypeCode">Catalogue 06 code; empty when the document has no buyer identification.</param>
/// <param name="BuyerDocumentNumber">Buyer identification number; empty when absent.</param>
/// <param name="DigestValue">Base64 value of the <c>ds:DigestValue</c> of the signed XML.</param>
public sealed record QrData(
    string IssuerRuc,
    string DocumentTypeCode,
    string Series,
    string Number,
    decimal TotalIgv,
    decimal TotalAmount,
    DateOnly IssueDate,
    string? BuyerDocumentTypeCode,
    string? BuyerDocumentNumber,
    string DigestValue);

/// <summary>
/// Builds the text encoded in the QR code. The QR symbology (QR Code 2005, ISO/IEC 18004:2006, error correction level Q, UTF-8,
/// at most 6 × 6 cm) belongs to the renderer; this service only owns the content (Anexo N.° 6 §6.4, S19).
/// </summary>
public interface IQrPayloadGenerator
{
    Result<string> Build(QrData data);
}
