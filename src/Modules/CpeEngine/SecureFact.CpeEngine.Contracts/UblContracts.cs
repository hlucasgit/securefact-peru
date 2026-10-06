using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>One installment of a credit sale: the amount due and its due date.</summary>
public sealed record UblInstallment(decimal Amount, DateOnly DueDate);

/// <summary>Detraction of an invoice as stated in the XML: catalogue 54 code, percentage, amount in soles and the issuer's Banco de la Nación account.</summary>
public sealed record UblDetraction(string GoodsOrServiceCode, decimal Percentage, decimal Amount, string AccountNumber);

/// <summary>IGV withholding as stated in the XML (catalogue 53 code 62): percentage, operation amount and amount withheld, in the document currency.</summary>
public sealed record UblRetention(decimal Percentage, decimal BaseAmount, decimal Amount);

/// <summary>Fishing resource of a line of an operation 1002: vessel, species, place and date of unloading and the quantity in metric tonnes (catalogue 55 codes 3001–3006).</summary>
public sealed record UblFishing(string VesselRegistration, string VesselName, string SpeciesType, string UnloadingPlace, DateOnly UnloadingDate, decimal SpeciesQuantity);

/// <summary>
/// The guest of a line of a lodging (0202) or tourist package (0205) export: name, identity document and passport country (codes 4007, 4008, 4009 and 4000), and in a lodging the residence
/// country, the dates of entry to the country, check-in, check-out and consumption and the days of stay (4001, 4002, 4003, 4004, 4006 and 4005).
/// </summary>
public sealed record UblGuest(
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

/// <summary>One leg of a cargo transport with its vehicle: origin and destination ubigeos, vehicle configuration, loads in metric tonnes and the preliminary reference values in soles.</summary>
public sealed record UblTransportLeg(
    string OriginUbigeo,
    string DestinationUbigeo,
    string VehicleConfiguration,
    decimal UsefulLoadTonnes,
    string? Description = null,
    decimal? EffectiveLoadTonnes = null,
    decimal? EffectiveLoadReferenceValue = null,
    decimal? NominalLoadReferenceValue = null,
    bool ReturnEmpty = false);

/// <summary>Cargo transport of a line of an operation 1004: origin and destination (ubigeo and address), trip detail, the three reference values in soles and the legs of the trip.</summary>
public sealed record UblCargoTransport(
    string OriginUbigeo,
    string OriginAddress,
    string DestinationUbigeo,
    string DestinationAddress,
    string TripDetail,
    decimal ServiceReferenceValue,
    decimal EffectiveLoadReferenceValue,
    decimal NominalLoadReferenceValue,
    IReadOnlyList<UblTransportLeg>? Legs = null);

public sealed record UblParty(string DocumentTypeCode, string DocumentNumber, string LegalName, string? TradeName = null, string? EstablishmentCode = null);

/// <param name="DiscountAffectingBase">Line discount, code 00 of catalogue 53 (the taxable base is reduced).</param>
/// <param name="ChargeAffectingBase">Line charge, code 47.</param>
/// <param name="DiscountNotAffectingBase">Line discount, code 01 (the base is untouched; the unit price with taxes reflects it).</param>
/// <param name="ChargeNotAffectingBase">Line charge, code 48.</param>
/// <param name="Isc">Selective consumption tax of the line as given to the TaxEngine (system and rate or unit amount); the amount comes from the calculated totals.</param>
/// <param name="PlasticBagCount">Plastic bags of the line subject to the ICBPER; it equals the quantity of the line (rule 3236).</param>
public sealed record UblLine(
    int LineNumber,
    string Description,
    string UnitCode,
    string? ProductCode,
    decimal Quantity,
    decimal UnitValue,
    decimal? ReferenceUnitValue,
    string IgvAffectationCode,
    decimal DiscountAffectingBase = 0m,
    decimal ChargeAffectingBase = 0m,
    decimal DiscountNotAffectingBase = 0m,
    decimal ChargeNotAffectingBase = 0m,
    UblFishing? Fishing = null,
    UblCargoTransport? Transport = null,
    UblGuest? Guest = null,
    IscInput? Isc = null,
    int PlasticBagCount = 0);

/// <summary>
/// Canonical input of the UBL generator. Amounts come from the TaxEngine result, never recomputed here: the XML must say exactly
/// what was calculated and numbered (§17: no XML straight from API DTOs).
/// </summary>
/// <param name="OperationTypeCode">Catalogue 51 code, e.g. <c>0101</c> internal sale.</param>
/// <param name="PaymentForm">Forma de pago of an invoice: <c>Contado</c>, or <c>Credito</c> together with <paramref name="Installments"/> (SUNAT rules 3244–3267, 3319).</param>
/// <param name="IvapRate">Rate of the IVAP (rice) as a fraction, for lines with affectation 17; the lines state it in <c>cbc:Percent</c> (rule 3103).</param>
/// <param name="Installments">The installments of a credit sale: at least one, each due after the issue date, adding up to the payable amount minus <paramref name="InitialPayment"/>.</param>
/// <param name="Detraction">Detraction of the invoice: the operation type is then 1001–1004 (by its code), the XML names the account and the amount and carries legend 2006.</param>
/// <param name="Retention">IGV withholding of the invoice, stated as a global allowance of code 62; it does not change the payable amount.</param>
/// <param name="UsageCountryCode">Country where the service of an export of services 0201 or 0208 is used, exploited or taken advantage of (ISO 3166-1 alpha-2, not PE); stated in the delivery location (rules 3098, 3099).</param>
/// <param name="LegendCodes">Legends of the exonerated sales (catalogue 52: 2001, 2002, 2003, 2008), each stated as a <c>cbc:Note</c> with its catalogue text; the document then has exonerated operations.</param>
/// <param name="InitialPayment">Part of a credit sale paid on the issue date (not stated in the XML: the net pending amount, the sum of the installments, already excludes it).</param>
/// <param name="Adjustments">Global discounts and charges (catalogue 53 codes 02, 03, 49, 50) exactly as given to the TaxEngine; the generator states them and checks that they agree with <paramref name="Totals"/>.</param>
/// <param name="IcbperUnitAmount">Amount per plastic bag of the ICBPER in force on the issue date (rule 4237); needed when a line has bags.</param>
public sealed record UblInvoiceData(
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    TimeOnly? IssueTime,
    string Currency,
    string OperationTypeCode,
    UblParty Issuer,
    UblParty Buyer,
    IReadOnlyList<UblLine> Lines,
    TaxCalculationResult Totals,
    decimal IgvRate,
    string PaymentForm = "Contado",
    GlobalAdjustments? Adjustments = null,
    IReadOnlyList<UblInstallment>? Installments = null,
    decimal IvapRate = 0m,
    decimal InitialPayment = 0m,
    UblDetraction? Detraction = null,
    UblRetention? Retention = null,
    string? UsageCountryCode = null,
    IReadOnlyList<string>? LegendCodes = null,
    decimal IcbperUnitAmount = 0m);

/// <summary>An unsigned UBL 2.1 document and the file names SUNAT expects for it.</summary>
public sealed record UblDocument(string Xml, string FileBaseName)
{
    public string XmlFileName => FileBaseName + ".xml";

    public string ZipFileName => FileBaseName + ".zip";
}

/// <summary>
/// Canonical input of a credit (07) or debit (08) note. The note modifies exactly one invoice (01) or receipt (03); the reason comes from
/// catalogue 09 (credit) or 10 (debit) and is explained in <see cref="ReasonDescription"/> (1-500 characters). A credit note of reason 13 (adjustment of the
/// amounts or dates of the installments) modifies an invoice sold on credit, has a payable amount of zero and states the new <paramref name="Installments"/>.
/// </summary>
public sealed record UblNoteData(
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    TimeOnly? IssueTime,
    string Currency,
    string ReasonCode,
    string ReasonDescription,
    string ReferencedDocumentTypeCode,
    string ReferencedSeries,
    long ReferencedNumber,
    UblParty Issuer,
    UblParty Buyer,
    IReadOnlyList<UblLine> Lines,
    TaxCalculationResult Totals,
    decimal IgvRate,
    IReadOnlyList<UblInstallment>? Installments = null,
    decimal IvapRate = 0m,
    decimal IcbperUnitAmount = 0m);

public interface IUblDocumentGenerator
{
    /// <summary>Generates the unsigned XML of a credit or debit note (UBL 2.1 CreditNote / DebitNote). Unsupported combinations fail explicitly.</summary>
    Result<UblDocument> GenerateNote(UblNoteData data);

    /// <summary>Generates the unsigned XML for an invoice (01) or receipt (03). Unsupported combinations fail explicitly.</summary>
    Result<UblDocument> GenerateInvoice(UblInvoiceData data);
}
