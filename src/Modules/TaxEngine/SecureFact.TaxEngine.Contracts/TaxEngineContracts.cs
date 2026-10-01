using SecureFact.SharedKernel.Results;

namespace SecureFact.TaxEngine.Contracts;

/// <summary>
/// Tax and concept codes of SUNAT catalogue No. 05 (Anexo N.°8 of the validation rules workbook, S16 in docs/regulatory/sources.md).
/// </summary>
public static class TaxCodes
{
    public const string Igv = "1000";
    public const string Ivap = "1016";
    public const string Isc = "2000";
    public const string Icbper = "7152";
    public const string Export = "9995";
    public const string Free = "9996";
    public const string Exempt = "9997";
    public const string Unaffected = "9998";
    public const string Other = "9999";
}

/// <summary>ISC calculation systems (SUNAT catalogue No. 08).</summary>
public enum IscSystem
{
    /// <summary>01 – ad valorem: rate × base.</summary>
    AdValorem = 1,

    /// <summary>02 – fixed amount per unit.</summary>
    FixedAmount = 2,
}

/// <summary>
/// Rates and the rounding policy are inputs, not constants: they come from versioned rules evaluated at the issue date (ADR-008).
/// Rates are fractions (0.18 = 18 %).
/// </summary>
public sealed record TaxRates(decimal IgvRate, decimal IvapRate = 0m, decimal IcbperUnitAmount = 0m);

public sealed record IscInput(IscSystem System, decimal RateOrUnitAmount);

/// <summary>One document line. Amounts are in the document currency; <see cref="UnitValue"/> excludes every tax.</summary>
/// <param name="Quantity">Positive, at most 10 decimals.</param>
/// <param name="UnitValue">Unit value without taxes, non-negative, at most 10 decimals. For free operations it is ignored in favour of <paramref name="ReferenceUnitValue"/>.</param>
/// <param name="IgvAffectationCode">SUNAT catalogue No. 07 code (10, 11–16, 17, 20, 21, 30–37, 40).</param>
/// <param name="DiscountAffectingBase">Line discount, charge code 00: reduces the taxable base.</param>
/// <param name="ChargeAffectingBase">Line charge, code 47: increases the taxable base.</param>
/// <param name="DiscountNotAffectingBase">Line discount, code 01: does not change the taxable base.</param>
/// <param name="ChargeNotAffectingBase">Line charge, code 48: does not change the taxable base.</param>
/// <param name="ReferenceUnitValue">Required for free operations (affectation codes that map to tax 9996).</param>
/// <param name="Isc">Selective consumption tax for the line, when applicable.</param>
/// <param name="PlasticBagCount">Number of plastic bags subject to ICBPER (tax 7152), when applicable.</param>
public sealed record TaxableLine(
    decimal Quantity,
    decimal UnitValue,
    string IgvAffectationCode,
    decimal DiscountAffectingBase = 0m,
    decimal ChargeAffectingBase = 0m,
    decimal DiscountNotAffectingBase = 0m,
    decimal ChargeNotAffectingBase = 0m,
    decimal? ReferenceUnitValue = null,
    IscInput? Isc = null,
    int PlasticBagCount = 0);

/// <summary>Document-level adjustments. Prepayments (codes 04/05/06/20) are not supported yet and are rejected explicitly.</summary>
/// <param name="DiscountAffectingBase">Code 02.</param>
/// <param name="ChargeAffectingBase">Code 49.</param>
/// <param name="DiscountNotAffectingBase">Code 03.</param>
/// <param name="ChargeNotAffectingBase">Code 50.</param>
/// <param name="PayableRoundingAmount">Optional rounding of the payable total; its absolute value may not exceed 1.</param>
public sealed record GlobalAdjustments(
    decimal DiscountAffectingBase = 0m,
    decimal ChargeAffectingBase = 0m,
    decimal DiscountNotAffectingBase = 0m,
    decimal ChargeNotAffectingBase = 0m,
    decimal PayableRoundingAmount = 0m);

public sealed record TaxCalculationRequest(
    IReadOnlyList<TaxableLine> Lines,
    TaxRates Rates,
    GlobalAdjustments? Adjustments = null);

public sealed record TaxSubtotal(string TaxCode, decimal TaxableAmount, decimal TaxAmount);

public sealed record LineTaxResult(
    int LineNumber,
    /// <summary>Valor de venta por ítem (LineExtensionAmount).</summary>
    decimal LineExtensionAmount,
    string TaxCode,
    decimal IscAmount,
    decimal IgvOrIvapAmount,
    decimal IcbperAmount,
    decimal TotalTaxAmount,
    /// <summary>Precio de venta unitario incluidos tributos (10 decimals); null for free operations.</summary>
    decimal? UnitPriceIncludingTaxes);

public sealed record TaxCalculationResult(
    IReadOnlyList<LineTaxResult> Lines,
    IReadOnlyList<TaxSubtotal> TaxSubtotals,
    decimal TotalTaxableGravado,
    decimal TotalExempt,
    decimal TotalUnaffected,
    decimal TotalExport,
    decimal TotalFree,
    decimal TotalIgv,
    decimal TotalIvap,
    decimal TotalIsc,
    decimal TotalIcbper,
    /// <summary>Sum of taxes 1000, 1016, 2000, 7152 and 9999 (cac:TaxTotal/cbc:TaxAmount).</summary>
    decimal TotalTaxAmount,
    /// <summary>Total valor de venta (LegalMonetaryTotal/LineExtensionAmount).</summary>
    decimal TotalLineExtensionAmount,
    /// <summary>Total precio de venta (LegalMonetaryTotal/TaxInclusiveAmount).</summary>
    decimal TotalTaxInclusiveAmount,
    decimal TotalAllowances,
    decimal TotalCharges,
    decimal PayableRoundingAmount,
    /// <summary>Importe total (LegalMonetaryTotal/PayableAmount).</summary>
    decimal PayableAmount);

/// <summary>Pure, deterministic calculation of line and document taxes. No I/O, no clock, no hidden constants.</summary>
public interface ITaxCalculator
{
    Result<TaxCalculationResult> Calculate(TaxCalculationRequest request);
}
