using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.TaxEngine;

/// <summary>
/// Calculation model derived from the official SUNAT validation rules (Factura2_0, workbook of 2026-08-26, S16):
/// line value = quantity × unit value − discounts(00) + charges(47); line IGV = rate × (line value + line ISC);
/// document IGV = rate × (Σ line IGV bases − global discounts(02) + global charges(49));
/// payable = tax-inclusive total + charges − allowances + rounding.
/// Money uses <see cref="decimal"/> only. Amounts are rounded to 2 decimals and unit values to 10 decimals with
/// <see cref="MidpointRounding.AwayFromZero"/>; the rounding mode is an assumption tracked as R-024 in docs/regulatory/matrix.md.
/// </summary>
internal sealed class TaxCalculator : ITaxCalculator
{
    private const int AmountDecimals = 2;
    private const int UnitDecimals = 10;
    private const decimal MaxAmount = 999_999_999_999.99m;

    private sealed record LineKind(string TaxCode, bool IsFree, bool ComputesIgv, bool IsIvap);

    private static readonly Dictionary<string, LineKind> Affectations = BuildAffectations();

    /// <summary>Catalogue 07 codes the calculator understands; a test keeps this in sync with the official catalogue.</summary>
    internal static IReadOnlyCollection<string> SupportedAffectationCodes => Affectations.Keys;

    public Result<TaxCalculationResult> Calculate(TaxCalculationRequest request)
    {
        try
        {
            return CalculateCore(request);
        }
        catch (OverflowException)
        {
            return Fail(ErrorCodes.TaxAmountOverflow, "Importe fuera de rango", "Un importe excede la capacidad de cálculo (máximo 12 enteros y 2 decimales).");
        }
    }

    private static Result<TaxCalculationResult> CalculateCore(TaxCalculationRequest request)
    {
        if (request?.Lines is not { Count: > 0 })
        {
            return Fail(ErrorCodes.TaxInvalidInput, "Sin líneas", "El comprobante debe tener al menos una línea.");
        }

        if (ValidateRates(request.Rates) is { } badRates)
        {
            return badRates;
        }

        var adjustments = request.Adjustments ?? new GlobalAdjustments();
        if (ValidateAdjustments(adjustments) is { } badAdjustments)
        {
            return badAdjustments;
        }

        var lines = new List<LineTaxResult>(request.Lines.Count);
        decimal gravado = 0, ivapBase = 0, exempt = 0, unaffected = 0, export = 0, free = 0;
        decimal igvBase = 0, isc = 0, iscBase = 0, icbper = 0, freeIgv = 0, allowances = 0, charges = 0;
        var hasIgvLine = false;
        var hasIvapLine = false;

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var number = i + 1;
            var line = request.Lines[i];
            var computed = CalculateLine(number, line, request.Rates);
            if (!computed.IsSuccess)
            {
                return computed.Error;
            }

            var (result, iscAmount) = computed.Value;
            lines.Add(result);

            switch (result.TaxCode)
            {
                case TaxCodes.Igv:
                    gravado += result.LineExtensionAmount;
                    igvBase += result.LineExtensionAmount + iscAmount;
                    hasIgvLine = true;
                    break;
                case TaxCodes.Ivap:
                    ivapBase += result.LineExtensionAmount;
                    hasIvapLine = true;
                    break;
                case TaxCodes.Exempt:
                    exempt += result.LineExtensionAmount;
                    break;
                case TaxCodes.Unaffected:
                    unaffected += result.LineExtensionAmount;
                    break;
                case TaxCodes.Export:
                    export += result.LineExtensionAmount;
                    break;
                case TaxCodes.Free:
                    free += result.LineExtensionAmount;
                    freeIgv += result.IgvOrIvapAmount;
                    break;
            }

            if (iscAmount > 0)
            {
                isc += iscAmount;
                iscBase += result.LineExtensionAmount;
            }

            icbper += result.IcbperAmount;
            allowances += line.DiscountNotAffectingBase;
            charges += line.ChargeNotAffectingBase;
        }

        if (hasIgvLine && hasIvapLine)
        {
            return Fail(ErrorCodes.TaxUnsupported, "Combinación no soportada", "No se admiten líneas afectas a IGV e IVAP en el mismo comprobante (pendiente de verificar en la norma).");
        }

        var adjustment = adjustments.ChargeAffectingBase - adjustments.DiscountAffectingBase;
        var adjustableBase = hasIvapLine ? ivapBase : gravado;
        if (adjustment != 0 && !hasIgvLine && !hasIvapLine)
        {
            return Fail(ErrorCodes.TaxInvalidInput, "Ajuste sin base", "Los descuentos/cargos globales que afectan la base requieren líneas gravadas.");
        }

        if (adjustableBase + adjustment < 0 || (hasIgvLine && igvBase + adjustment < 0))
        {
            return Fail(ErrorCodes.TaxInvalidInput, "Descuento excesivo", "El descuento global que afecta la base excede el valor de venta gravado.");
        }

        decimal totalIgv = 0, totalIvap = 0;
        if (hasIgvLine)
        {
            gravado += adjustment;
            totalIgv = Round2(request.Rates.IgvRate * (igvBase + adjustment));
        }
        else if (hasIvapLine)
        {
            ivapBase += adjustment;
            totalIvap = Round2(request.Rates.IvapRate * ivapBase);
        }

        var totalTax = totalIgv + totalIvap + isc + icbper;
        var lineExtension = gravado + ivapBase + export + exempt + unaffected;
        var taxInclusive = lineExtension + totalTax;
        var totalAllowances = allowances + adjustments.DiscountNotAffectingBase;
        var totalCharges = charges + adjustments.ChargeNotAffectingBase;
        var payable = taxInclusive + totalCharges - totalAllowances + adjustments.PayableRoundingAmount;

        if (payable < 0)
        {
            return Fail(ErrorCodes.TaxInvalidInput, "Importe total negativo", "Los descuentos superan el importe del comprobante.");
        }

        if (Math.Max(payable, Math.Max(taxInclusive, lineExtension)) > MaxAmount)
        {
            return Fail(ErrorCodes.TaxAmountOverflow, "Importe fuera de rango", "Un total excede 12 enteros y 2 decimales.");
        }

        var subtotals = new List<TaxSubtotal>();
        if (hasIgvLine)
        {
            subtotals.Add(new TaxSubtotal(TaxCodes.Igv, gravado, totalIgv));
        }

        if (hasIvapLine)
        {
            subtotals.Add(new TaxSubtotal(TaxCodes.Ivap, ivapBase, totalIvap));
        }

        if (isc > 0)
        {
            subtotals.Add(new TaxSubtotal(TaxCodes.Isc, iscBase, isc));
        }

        if (icbper > 0)
        {
            subtotals.Add(new TaxSubtotal(TaxCodes.Icbper, 0m, icbper));
        }

        AddIfPositive(subtotals, TaxCodes.Export, export);
        AddIfPositive(subtotals, TaxCodes.Free, free, freeIgv);
        AddIfPositive(subtotals, TaxCodes.Exempt, exempt);
        AddIfPositive(subtotals, TaxCodes.Unaffected, unaffected);

        return new TaxCalculationResult(
            lines, subtotals, gravado, exempt, unaffected, export, free,
            totalIgv, totalIvap, isc, icbper, totalTax, lineExtension, taxInclusive,
            totalAllowances, totalCharges, adjustments.PayableRoundingAmount, payable);
    }

    private static Result<(LineTaxResult Result, decimal Isc)> CalculateLine(int number, TaxableLine line, TaxRates rates)
    {
        static Result<(LineTaxResult, decimal)> Bad(string code, int n, string detail) =>
            Error.Validation(code, $"Línea {n} inválida", detail);

        if (line is null || !Affectations.TryGetValue(line.IgvAffectationCode ?? string.Empty, out var kind))
        {
            return Bad(ErrorCodes.TaxUnsupported, number, $"El código de afectación '{line?.IgvAffectationCode}' no es válido (catálogo 07).");
        }

        if (line.Quantity <= 0 || Scale(line.Quantity) > UnitDecimals)
        {
            return Bad(line.Quantity <= 0 ? ErrorCodes.TaxInvalidInput : ErrorCodes.TaxPrecisionExceeded, number, "La cantidad debe ser mayor que cero y tener hasta 10 decimales.");
        }

        var unitValue = line.UnitValue;
        if (kind.IsFree)
        {
            if (line.ReferenceUnitValue is not > 0 || Scale(line.ReferenceUnitValue.Value) > UnitDecimals)
            {
                return Bad(ErrorCodes.TaxInvalidInput, number, "Las operaciones gratuitas requieren un valor referencial unitario mayor que cero (hasta 10 decimales).");
            }

            unitValue = line.ReferenceUnitValue.Value;
        }
        else if (unitValue < 0 || Scale(unitValue) > UnitDecimals)
        {
            return Bad(unitValue < 0 ? ErrorCodes.TaxInvalidInput : ErrorCodes.TaxPrecisionExceeded, number, "El valor unitario debe ser no negativo y tener hasta 10 decimales.");
        }

        foreach (var amount in new[] { line.DiscountAffectingBase, line.ChargeAffectingBase, line.DiscountNotAffectingBase, line.ChargeNotAffectingBase })
        {
            if (amount < 0 || Scale(amount) > AmountDecimals)
            {
                return Bad(amount < 0 ? ErrorCodes.TaxInvalidInput : ErrorCodes.TaxPrecisionExceeded, number, "Los descuentos y cargos deben ser no negativos y tener hasta 2 decimales.");
            }
        }

        if (line.PlasticBagCount < 0)
        {
            return Bad(ErrorCodes.TaxInvalidInput, number, "La cantidad de bolsas de plástico no puede ser negativa.");
        }

        if (line.Isc is not null && kind.TaxCode != TaxCodes.Igv)
        {
            return Bad(ErrorCodes.TaxUnsupported, number, "El ISC solo se admite en líneas gravadas con IGV (afectación 10).");
        }

        if (line.PlasticBagCount > 0 && kind.IsFree)
        {
            return Bad(ErrorCodes.TaxUnsupported, number, "El ICBPER no se admite en operaciones gratuitas.");
        }

        var gross = line.Quantity * unitValue;
        var net = gross - line.DiscountAffectingBase + line.ChargeAffectingBase;
        if (net < 0)
        {
            return Bad(ErrorCodes.TaxInvalidInput, number, "El descuento que afecta la base excede el valor de la línea.");
        }

        var lineValue = Round2(net);
        if (lineValue > MaxAmount)
        {
            return Bad(ErrorCodes.TaxAmountOverflow, number, "El valor de venta de la línea excede 12 enteros y 2 decimales.");
        }

        decimal iscAmount = 0;
        if (line.Isc is { } isc)
        {
            if (isc.RateOrUnitAmount <= 0)
            {
                return Bad(ErrorCodes.TaxInvalidInput, number, "La tasa o monto unitario del ISC debe ser mayor que cero.");
            }

            iscAmount = isc.System switch
            {
                IscSystem.AdValorem => Round2(isc.RateOrUnitAmount * lineValue),
                IscSystem.FixedAmount => Round2(isc.RateOrUnitAmount * line.Quantity),
                _ => throw new InvalidOperationException("Unknown ISC system."),
            };
        }

        decimal igvOrIvap = 0;
        if (kind.IsIvap)
        {
            igvOrIvap = Round2(rates.IvapRate * lineValue);
        }
        else if (kind.ComputesIgv)
        {
            igvOrIvap = Round2(rates.IgvRate * (lineValue + iscAmount));
        }

        var icbper = Round2(rates.IcbperUnitAmount * line.PlasticBagCount);
        var lineTax = kind.IsFree ? 0m : iscAmount + igvOrIvap + icbper;

        decimal? unitPrice = kind.IsFree
            ? null
            : Round(lineValue + lineTax - line.DiscountNotAffectingBase + line.ChargeNotAffectingBase, UnitDecimals, line.Quantity);

        var result = new LineTaxResult(number, lineValue, kind.TaxCode, iscAmount, igvOrIvap, icbper, lineTax, unitPrice);
        return (result, iscAmount);
    }

    private static Error? ValidateRates(TaxRates rates)
    {
        if (rates is null || rates.IgvRate is < 0 or >= 1 || rates.IvapRate is < 0 or >= 1 || rates.IcbperUnitAmount < 0)
        {
            return Error.Validation(ErrorCodes.TaxInvalidInput, "Tasas inválidas", "Las tasas de IGV/IVAP deben estar entre 0 y 1 y el monto de ICBPER no puede ser negativo.");
        }

        return null;
    }

    private static Error? ValidateAdjustments(GlobalAdjustments a)
    {
        foreach (var amount in new[] { a.DiscountAffectingBase, a.ChargeAffectingBase, a.DiscountNotAffectingBase, a.ChargeNotAffectingBase })
        {
            if (amount < 0 || Scale(amount) > AmountDecimals)
            {
                return Error.Validation(ErrorCodes.TaxPrecisionExceeded, "Ajuste global inválido", "Los descuentos y cargos globales deben ser no negativos y tener hasta 2 decimales.");
            }
        }

        if (Math.Abs(a.PayableRoundingAmount) > 1 || Scale(a.PayableRoundingAmount) > AmountDecimals)
        {
            return Error.Validation(ErrorCodes.TaxInvalidInput, "Redondeo inválido", "El redondeo del importe total no puede exceder 1 en valor absoluto ni tener más de 2 decimales.");
        }

        return null;
    }

    private static void AddIfPositive(List<TaxSubtotal> subtotals, string code, decimal taxable, decimal tax = 0m)
    {
        if (taxable > 0)
        {
            subtotals.Add(new TaxSubtotal(code, taxable, tax));
        }
    }

    private static Result<TaxCalculationResult> Fail(string code, string title, string detail) => Error.Validation(code, title, detail);

    private static decimal Round2(decimal value) => Math.Round(value, AmountDecimals, MidpointRounding.AwayFromZero);

    private static decimal Round(decimal numerator, int decimals, decimal denominator) =>
        Math.Round(numerator / denominator, decimals, MidpointRounding.AwayFromZero);

    /// <summary>Number of significant decimal places (trailing zeros ignored).</summary>
    private static int Scale(decimal value)
    {
        var normalized = value / 1.000000000000000000000000000000000m;
        return (decimal.GetBits(normalized)[3] >> 16) & 0xFF;
    }

    private static Dictionary<string, LineKind> BuildAffectations()
    {
        // SUNAT catalogue No. 07 (Anexo N.°8, S16). Free operations report under tax 9996; only 11–16 compute an informational IGV.
        var map = new Dictionary<string, LineKind>(StringComparer.Ordinal)
        {
            ["10"] = new(TaxCodes.Igv, false, true, false),
            ["17"] = new(TaxCodes.Ivap, false, false, true),
            ["20"] = new(TaxCodes.Exempt, false, false, false),
            ["21"] = new(TaxCodes.Free, true, false, false),
            ["30"] = new(TaxCodes.Unaffected, false, false, false),
            ["40"] = new(TaxCodes.Export, false, false, false),
        };
        foreach (var code in new[] { "11", "12", "13", "14", "15", "16" })
        {
            map[code] = new LineKind(TaxCodes.Free, true, true, false);
        }

        foreach (var code in new[] { "31", "32", "33", "34", "35", "36", "37" })
        {
            map[code] = new LineKind(TaxCodes.Free, true, false, false);
        }

        return map;
    }
}
