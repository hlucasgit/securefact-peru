using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Billing.Contracts;

/// <summary>SUNAT catalogue No. 01 codes handled by this module. Notes (07/08) arrive with the CDR-aware flow in Phase 4.</summary>
public static class DocumentTypes
{
    public const string Invoice = "01";
    public const string Receipt = "03";
    public const string CreditNote = "07";
    public const string DebitNote = "08";
}

/// <summary>Catalogue 51 operation types an invoice can be issued with today.</summary>
public static class OperationTypes
{
    /// <summary>0101 – internal sale.</summary>
    public const string Sale = "0101";

    /// <summary>0200 – export of goods (the sale is not taxed: lines with affectation 40, tax 9995).</summary>
    public const string Export = "0200";

    /// <summary>0201 – export of services, rendered entirely in the country. Names the country where the service is used (<see cref="CreateDocumentRequest.UsageCountryCode"/>).</summary>
    public const string ExportServicesInCountry = "0201";

    /// <summary>0203 – export of services, transport of shipping lines.</summary>
    public const string ExportShippingLines = "0203";

    /// <summary>0204 – export of services, services to foreign-flag ships and aircraft.</summary>
    public const string ExportForeignCraftServices = "0204";

    /// <summary>0206 – export of services, services complementary to cargo transport.</summary>
    public const string ExportCargoSupport = "0206";

    /// <summary>0207 – export of services, supply of electric power to subjects domiciled in an economic special zone (ZED).</summary>
    public const string ExportZedElectricity = "0207";

    /// <summary>0208 – export of services, rendered partly abroad. Names the country where the service is used.</summary>
    public const string ExportServicesPartlyAbroad = "0208";

    /// <summary>0202 – export of services, lodging of non-domiciled guests (invoices only). Every line carries a <see cref="GuestDetail"/> with the stay.</summary>
    public const string ExportLodging = "0202";

    /// <summary>0205 – export of services that make up a tourist package (invoices only). Every line carries a <see cref="GuestDetail"/> with the guest only.</summary>
    public const string ExportTouristPackage = "0205";

    /// <summary>The types that only an invoice can carry (catalogue 51): lodging and tourist package.</summary>
    public static bool IsInvoiceOnlyExport(string? operationTypeCode) => operationTypeCode is ExportLodging or ExportTouristPackage;

    /// <summary>
    /// The export types the platform issues: goods (0200) and the services above, in invoices and receipts, and the lodging (0202) and tourist package (0205) exports, in invoices only.
    /// </summary>
    public static bool IsExport(string? operationTypeCode) =>
        operationTypeCode is Export or ExportServicesInCountry or ExportShippingLines or ExportForeignCraftServices or ExportCargoSupport or ExportZedElectricity or ExportServicesPartlyAbroad
            or ExportLodging or ExportTouristPackage;

    /// <summary>The types whose buyer lives abroad: the sheet forbids the RUC there (rule 2800) unless the legend 2008 (Tacna commercial zone) is stated.</summary>
    public static bool RequiresForeignBuyer(string? operationTypeCode) => operationTypeCode is Export or ExportServicesInCountry or ExportForeignCraftServices;

    /// <summary>The types that name the country where the service is used, exploited or taken advantage of (rules 3098, 3099).</summary>
    public static bool RequiresUsageCountry(string? operationTypeCode) => operationTypeCode is ExportServicesInCountry or ExportServicesPartlyAbroad;

    /// <summary>1001 – operation subject to detraction. Implied by a <see cref="Detraction"/> and never given by the client without one.</summary>
    public const string SaleWithDetraction = "1001";

    /// <summary>1002 – operation subject to detraction, fishing resources (catalogue 54 code 004). Every line carries a <see cref="FishingDetail"/>.</summary>
    public const string FishingDetraction = "1002";

    /// <summary>1003 – operation subject to detraction, passenger transport (catalogue 54 code 028).</summary>
    public const string PassengerTransportDetraction = "1003";

    /// <summary>1004 – operation subject to detraction, cargo transport (catalogue 54 code 027). Every line carries a <see cref="CargoTransportDetail"/>.</summary>
    public const string CargoTransportDetraction = "1004";

    /// <summary>The operation type that goes with a detraction of the given catalogue 54 code: 1002 for fishing (004), 1003 for passenger transport (028), 1004 for cargo transport (027) and 1001 for the rest.</summary>
    public static string ForDetraction(string? goodsOrServiceCode) => goodsOrServiceCode?.Trim() switch
    {
        "004" => FishingDetraction,
        "028" => PassengerTransportDetraction,
        "027" => CargoTransportDetraction,
        _ => SaleWithDetraction,
    };

    /// <summary>True for the four operation types subject to detraction (1001–1004).</summary>
    public static bool IsDetraction(string? operationTypeCode) => operationTypeCode is SaleWithDetraction or FishingDetraction or PassengerTransportDetraction or CargoTransportDetraction;
}

/// <summary>
/// Data of the fishing resource a line sells, required on every line of an operation 1002 (catalogue 55 codes 3001–3006). The quantity is in metric tonnes.
/// </summary>
/// <param name="VesselRegistration">Registration of the fishing vessel (1–15 characters).</param>
/// <param name="VesselName">Name of the fishing vessel (1–100).</param>
/// <param name="SpeciesType">Description of the species sold (1–150).</param>
/// <param name="UnloadingPlace">Place of unloading (1–100).</param>
/// <param name="UnloadingDate">Date of unloading.</param>
/// <param name="SpeciesQuantity">Quantity of the species sold, in metric tonnes (greater than zero, up to 2 decimals).</param>
public sealed record FishingDetail(string VesselRegistration, string VesselName, string SpeciesType, string UnloadingPlace, DateOnly UnloadingDate, decimal SpeciesQuantity);

/// <summary>
/// One leg (tramo) of a cargo transport and the vehicle that runs it, as SOL's "Información de Tramo y Vehículo" form registers them. The origin and destination are ubigeos (6 digits) and the
/// vehicle configuration (1–15 characters, codes of the D.S. 058-2003-MTC) and the useful load in metric tonnes are required; the description (3–100 characters), the effective load in
/// tonnes, the two preliminary reference values in soles (by effective load and by nominal useful load) and the return-empty factor are optional. Amounts are greater than zero, up to 2 decimals.
/// </summary>
public sealed record TransportLeg(
    string OriginUbigeo,
    string DestinationUbigeo,
    string VehicleConfiguration,
    decimal UsefulLoadTonnes,
    string? Description = null,
    decimal? EffectiveLoadTonnes = null,
    decimal? EffectiveLoadReferenceValue = null,
    decimal? NominalLoadReferenceValue = null,
    bool ReturnEmpty = false);

/// <summary>
/// Data of the cargo transport a line sells, required on every line of an operation 1004. The origin and destination come with their ubigeo (6 digits) and an
/// address of 3–200 characters, the trip detail has 3–500; the three reference values are in soles. The <paramref name="Legs"/> of the trip (at most 99), with their vehicles,
/// are optional in SUNAT's rules.
/// </summary>
public sealed record CargoTransportDetail(
    string OriginUbigeo,
    string OriginAddress,
    string DestinationUbigeo,
    string DestinationAddress,
    string TripDetail,
    decimal ServiceReferenceValue,
    decimal EffectiveLoadReferenceValue,
    decimal NominalLoadReferenceValue,
    IReadOnlyList<TransportLeg>? Legs = null);

/// <summary>
/// Legends of catalogue 52 that an issuer states on a sale of exonerated operations: the exoneration of the Amazon region (2001 goods, 2002 services, 2003 construction contracts) and the
/// one of the commercial zone of Tacna (2008). The sheets Factura2_0 and Boleta2_0 ask for the same thing in all four: the document carries operations exonerated from the IGV (tax 9997 with a
/// positive base; rules 3283–3285 and 3289 in invoices, observations 4022–4024 and 4244 in receipts). The texts are the ones of the catalogue.
/// </summary>
public static class ExemptionLegends
{
    /// <summary>2001 – goods transferred in the Amazon region to be consumed there.</summary>
    public const string AmazonGoods = "2001";

    /// <summary>2002 – services provided in the Amazon region to be consumed there.</summary>
    public const string AmazonServices = "2002";

    /// <summary>2003 – construction contracts executed in the Amazon region.</summary>
    public const string AmazonConstruction = "2003";

    /// <summary>2008 – exonerated sale in the commercial zone of Tacna.</summary>
    public const string TacnaCommercialZone = "2008";

    public static IReadOnlyDictionary<string, string> Texts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AmazonGoods] = "BIENES TRANSFERIDOS EN LA AMAZONÍA REGIÓN SELVA PARA SER CONSUMIDOS EN LA MISMA",
        [AmazonServices] = "SERVICIOS PRESTADOS EN LA AMAZONÍA REGIÓN SELVA PARA SER CONSUMIDOS EN LA MISMA",
        [AmazonConstruction] = "CONTRATOS DE CONSTRUCCIÓN EJECUTADOS EN LA AMAZONÍA REGIÓN SELVA",
        [TacnaCommercialZone] = "VENTA EXONERADA DEL IGV-ISC-IPM. PROHIBIDA LA VENTA FUERA DE LA ZONA COMERCIAL DE TACNA",
    };
}

/// <summary>
/// Detraction (SPOT) of an invoice: the buyer deposits <paramref name="Amount"/> in the issuer's account at the Banco de la Nación. SUNAT's rules check its structure but not the
/// percentage or the amount, so both are the issuer's data; the platform only checks that they agree. The amount is always in soles.
/// </summary>
/// <param name="GoodsOrServiceCode">Catalogue 54 code (e.g. <c>037</c>, other services taxed with the IGV).</param>
/// <param name="Percentage">As a percentage, e.g. <c>12</c> for 12 %.</param>
/// <param name="AccountNumber">Issuer's account number at the Banco de la Nación; when absent the one registered in the company is used (and kept in the issued document).</param>
public sealed record Detraction(string GoodsOrServiceCode, decimal Percentage, decimal Amount, string? AccountNumber = null);

/// <summary>IGV withholding that the buyer, a withholding agent, applies to the invoice: <paramref name="Percentage"/> of the payable amount (e.g. <c>3</c> for 3 %).</summary>
public sealed record RetentionRequest(decimal Percentage);

/// <summary>The IGV withholding as issued: its percentage, the operation amount it applies to (the payable amount) and the amount withheld.</summary>
public sealed record IgvRetention(decimal Percentage, decimal BaseAmount, decimal Amount);

public sealed record CreateSeriesRequest(Guid CompanyId, string DocumentTypeCode, string Code, Guid? EstablishmentId = null);

public sealed record SeriesDto(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    Guid? EstablishmentId,
    string DocumentTypeCode,
    string Code,
    long LastNumber,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface ISeriesAdministration
{
    Task<Result<SeriesDto>> CreateAsync(CreateSeriesRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesDto>> ListAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>A deactivated series is kept: issued numbers must stay traceable. It simply stops issuing.</summary>
    Task<Result<Unit>> DeactivateAsync(Guid seriesId, CancellationToken cancellationToken);
}

/// <summary>Buyer identity as it appears on the document (a snapshot: later edits to a customer never alter an issued document).</summary>
/// <param name="DocumentTypeCode">SUNAT catalogue No. 06 code: 6 RUC, 1 DNI, 4 carné de extranjería, 7 pasaporte, A cédula diplomática, 0 sin documento.</param>
public sealed record BuyerSnapshot(string DocumentTypeCode, string DocumentNumber, string Name, string? Address = null, string? Email = null);

public sealed record DocumentLineRequest(
    string Description,
    string UnitCode,
    TaxableLine Tax,
    string? ProductCode = null,
    FishingDetail? Fishing = null,
    CargoTransportDetail? Transport = null,
    GuestDetail? Guest = null);

/// <summary>
/// The non-domiciled guest of a lodging (0202) or tourist package (0205) line, with the concepts of catalogue 55 codes 4000–4009. The name (3–200 characters, code 4007), the identity
/// document type (catalogue 06, 4008) and number (3–20 characters, 4009) and the country that issued the passport (ISO 3166-1, 4000) are always required. A lodging (0202) also states the
/// country of residence (4001), the dates of entry to the country (4002), of check-in (4003), of check-out (4004, not before the check-in) and of consumption (4006), and the number of
/// days of stay (4005, up to 4 digits); a tourist package states none of those.
/// </summary>
public sealed record GuestDetail(
    string Name,
    string DocumentTypeCode,
    string DocumentNumber,
    string PassportCountryCode,
    string? ResidenceCountryCode = null,
    DateOnly? CountryEntryDate = null,
    DateOnly? CheckInDate = null,
    DateOnly? CheckOutDate = null,
    DateOnly? ConsumptionDate = null,
    int? StayDays = null);

/// <summary>One installment (cuota) of an invoice sold on credit: the amount due and the day it falls due.</summary>
public sealed record Installment(decimal Amount, DateOnly DueDate);

/// <summary>
/// Tax rates are never accepted from the client: the platform resolves them from versioned rules at the issue date.
/// The buyer is given either inline (<paramref name="Buyer"/>) or by reference (<paramref name="CustomerId"/>), never both; a referenced
/// customer is copied into the document as a snapshot. An invoice (never a receipt) is sold on credit when <paramref name="Installments"/> is given: its
/// amounts must add up to the payable amount and every due date must fall after the issue date. <paramref name="OperationTypeCode"/> is the catalogue 51 type:
/// <c>0101</c> by default, <c>0200</c> for the export of goods or <c>0201</c>, <c>0202</c>, <c>0203</c>, <c>0204</c>, <c>0205</c>, <c>0206</c>, <c>0207</c> and <c>0208</c> for the export of services (every line with
/// affectation 40; the buyer of an invoice has no RUC in 0200, 0201 and 0204, and the buyer of a receipt never has one).
/// <paramref name="LegendCodes"/> are the legends of <see cref="ExemptionLegends"/> (2001, 2002, 2003, 2008) the issuer states; each one needs exonerated operations in the document. <paramref name="UsageCountryCode"/> (ISO 3166-1 alpha-2, never <c>PE</c>) is required in 0201 and 0208 and refused elsewhere.
/// <paramref name="InitialPayment"/> is the part of a credit sale paid on the issue date (entrega inicial): the installments then add up to the payable amount minus it.
/// An invoice may carry a <paramref name="Detraction"/> or an IGV <paramref name="Retention"/>, never both; the net pending amount of a credit sale then
/// excludes them too. The operation type of a detraction follows its catalogue 54 code (see <see cref="OperationTypes.ForDetraction"/>): 1002 (fishing) requires
/// <see cref="DocumentLineRequest.Fishing"/> on every line and 1004 (cargo transport) <see cref="DocumentLineRequest.Transport"/>.
/// </summary>
public sealed record CreateDocumentRequest(
    Guid SeriesId,
    DateOnly IssueDate,
    string Currency,
    BuyerSnapshot? Buyer,
    IReadOnlyList<DocumentLineRequest> Lines,
    GlobalAdjustments? Adjustments = null,
    Guid? CustomerId = null,
    IReadOnlyList<Installment>? Installments = null,
    string? OperationTypeCode = null,
    decimal? InitialPayment = null,
    Detraction? Detraction = null,
    RetentionRequest? Retention = null,
    string? UsageCountryCode = null,
    IReadOnlyList<string>? LegendCodes = null);

/// <summary>
/// A credit (07) or debit (08) note. The series decides which; the note modifies one issued invoice or receipt, takes its currency and buyer, and
/// cannot be dated before it. <see cref="ReasonCode"/> is catalogue 09 (credit: 01-10 and 13) or 10 (debit: 01-03); <see cref="Reason"/> explains it.
/// Reason 13 adjusts the installments of an invoice sold on credit: it gives the new <paramref name="Installments"/> and no lines (the note carries a single line worth
/// zero, because nothing is sold or returned); every other reason gives lines and no installments.
/// </summary>
public sealed record CreateNoteRequest(
    Guid SeriesId,
    Guid ReferencedDocumentId,
    DateOnly IssueDate,
    string ReasonCode,
    string Reason,
    IReadOnlyList<DocumentLineRequest>? Lines,
    GlobalAdjustments? Adjustments = null,
    IReadOnlyList<Installment>? Installments = null);

/// <summary>What a note modifies and why.</summary>
public sealed record NoteInfo(string ReasonCode, string Reason, Guid ReferencedDocumentId, string ReferencedDocumentTypeCode, string ReferencedSeries, long ReferencedNumber);

public enum DocumentStatus
{
    /// <summary>Numbered and fully calculated; no electronic artefact exists yet (Phase 3 generates the XML).</summary>
    Validated,
}

/// <param name="DiscountAffectingBase">Line discount, charge code 00 (reduces the taxable base).</param>
/// <param name="ChargeAffectingBase">Line charge, code 47.</param>
/// <param name="DiscountNotAffectingBase">Line discount, code 01.</param>
/// <param name="ChargeNotAffectingBase">Line charge, code 48.</param>
public sealed record DocumentLineDto(
    int LineNumber,
    string Description,
    string UnitCode,
    string? ProductCode,
    decimal Quantity,
    decimal LineExtensionAmount,
    string TaxCode,
    decimal TotalTaxAmount,
    decimal? UnitPriceIncludingTaxes,
    decimal UnitValue = 0m,
    string IgvAffectationCode = "",
    decimal DiscountAffectingBase = 0m,
    decimal ChargeAffectingBase = 0m,
    decimal DiscountNotAffectingBase = 0m,
    decimal ChargeNotAffectingBase = 0m,
    FishingDetail? Fishing = null,
    CargoTransportDetail? Transport = null,
    GuestDetail? Guest = null);

public sealed record DocumentDto(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    string Currency,
    BuyerSnapshot Buyer,
    DocumentStatus Status,
    IReadOnlyList<DocumentLineDto> Lines,
    TaxCalculationResult Totals,
    DateTimeOffset CreatedAt,
    NoteInfo? Note = null,
    GlobalAdjustments? Adjustments = null,
    IReadOnlyList<Installment>? Installments = null,
    string OperationTypeCode = OperationTypes.Sale,
    decimal? InitialPayment = null,
    Detraction? Detraction = null,
    IgvRetention? Retention = null,
    string? UsageCountryCode = null,
    IReadOnlyList<string>? LegendCodes = null)
{
    public string FullNumber => $"{Series}-{Number}";
}

/// <summary>
/// Tells Billing whether a document has been annulled. The electronic-invoicing module implements it (the annulment lives in its records);
/// without it Billing treats no document as voided.
/// </summary>
public interface IVoidStatusProvider
{
    /// <summary>True when the document is voided, or when a request to void it (a voided-documents communication or a summary line of status 3) is still pending.</summary>
    Task<bool> IsVoidedOrBeingVoidedAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// Tells Billing which documents no longer count: the ones SUNAT rejected and the ones that were voided. The electronic-invoicing module implements it; without it
/// Billing counts every document.
/// </summary>
public interface IIneffectiveDocumentsProvider
{
    /// <summary>The subset of <paramref name="documentIds"/> that was rejected by SUNAT or is voided.</summary>
    Task<IReadOnlySet<Guid>> FindAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}

public interface IDocumentService
{
    /// <summary>
    /// Creates and numbers a document. Idempotent per <paramref name="idempotencyKey"/>: the same key with the same content returns the
    /// original document; the same key with different content is a conflict.
    /// </summary>
    Task<Result<DocumentDto>> CreateAsync(string idempotencyKey, CreateDocumentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Creates and numbers a credit or debit note with the same guarantees as <see cref="CreateAsync"/> (idempotent, gap-free, rates from rules,
    /// insert-only). A credit note cannot exceed the document it modifies.
    /// </summary>
    Task<Result<DocumentDto>> CreateNoteAsync(string idempotencyKey, CreateNoteRequest request, CancellationToken cancellationToken);

    Task<Result<DocumentDto>> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentDto>> ListAsync(Guid? companyId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Documents of one company, type and issue date, ordered by series and number (for daily summaries).</summary>
    Task<IReadOnlyList<DocumentDto>> ListIssuedAsync(Guid companyId, string documentTypeCode, DateOnly issueDate, int skip, int take, CancellationToken cancellationToken);
}

/// <summary>Integration event types published by Billing through its transactional outbox.</summary>
public static class BillingEvents
{
    /// <summary>A document was numbered and stored. Payload: <see cref="DocumentIssuedEvent"/> (JSON, web casing).</summary>
    public const string DocumentIssued = "billing.document.issued";
}

public sealed record DocumentIssuedEvent(Guid TenantId, Guid DocumentId, Guid CompanyId, string DocumentTypeCode, string Series, long Number);
