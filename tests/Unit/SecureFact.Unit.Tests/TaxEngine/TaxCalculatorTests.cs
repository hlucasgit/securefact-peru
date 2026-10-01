using SecureFact.SharedKernel;
using SecureFact.TaxEngine;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Unit.Tests.TaxEngine;

/// <summary>Hand-computed cases following the official SUNAT validation rules (see TaxCalculator remarks).</summary>
public class TaxCalculatorTests
{
    private static readonly TaxRates Rates = new(IgvRate: 0.18m, IvapRate: 0.04m, IcbperUnitAmount: 0.50m);
    private readonly TaxCalculator _calculator = new();

    private TaxCalculationResult Ok(TaxCalculationRequest request)
    {
        var result = _calculator.Calculate(request);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : $"{result.Error.Code}: {result.Error.Detail}");
        return result.Value;
    }

    private string ErrorOf(TaxCalculationRequest request) => _calculator.Calculate(request).Error.Code;

    private static TaxCalculationRequest Single(TaxableLine line, GlobalAdjustments? adjustments = null, TaxRates? rates = null) =>
        new([line], rates ?? Rates, adjustments);

    private static TaxableLine Taxed(decimal qty, decimal unit, string code = "10") => new(qty, unit, code);

    // ---------- happy paths ----------

    [Fact]
    public void Simple_taxed_line()
    {
        var r = Ok(Single(Taxed(2, 100m)));

        var line = Assert.Single(r.Lines);
        Assert.Equal(200.00m, line.LineExtensionAmount);
        Assert.Equal(36.00m, line.IgvOrIvapAmount);
        Assert.Equal(118.0000000000m, line.UnitPriceIncludingTaxes);
        Assert.Equal(200.00m, r.TotalTaxableGravado);
        Assert.Equal(36.00m, r.TotalIgv);
        Assert.Equal(36.00m, r.TotalTaxAmount);
        Assert.Equal(200.00m, r.TotalLineExtensionAmount);
        Assert.Equal(236.00m, r.TotalTaxInclusiveAmount);
        Assert.Equal(236.00m, r.PayableAmount);
    }

    [Fact]
    public void Line_value_is_rounded_to_two_decimals_from_the_exact_product()
    {
        var r = Ok(Single(Taxed(3, 0.3333333333m)));

        Assert.Equal(1.00m, r.Lines[0].LineExtensionAmount);
        Assert.Equal(0.18m, r.TotalIgv);
        Assert.Equal(1.18m, r.PayableAmount);
    }

    [Fact]
    public void Midpoints_round_away_from_zero()
    {
        // 0.125 x 1 = 0.125 -> 0.13 ; IGV base 0.13 x 0.18 = 0.0234 -> 0.02
        var r = Ok(Single(Taxed(1, 0.125m)));

        Assert.Equal(0.13m, r.Lines[0].LineExtensionAmount);
        Assert.Equal(0.02m, r.TotalIgv);
    }

    [Fact]
    public void Every_operation_type_is_reported_under_its_own_tax_code()
    {
        var request = new TaxCalculationRequest(
            [Taxed(1, 100m, "10"), Taxed(1, 50m, "20"), Taxed(1, 25m, "30"), Taxed(1, 10m, "40")], Rates);

        var r = Ok(request);

        Assert.Equal(100m, r.TotalTaxableGravado);
        Assert.Equal(50m, r.TotalExempt);
        Assert.Equal(25m, r.TotalUnaffected);
        Assert.Equal(10m, r.TotalExport);
        Assert.Equal(18m, r.TotalIgv);
        Assert.Equal(185m, r.TotalLineExtensionAmount);
        Assert.Equal(203m, r.PayableAmount);
        Assert.Equal(
            [("1000", 100m, 18m), ("9995", 10m, 0m), ("9997", 50m, 0m), ("9998", 25m, 0m)],
            r.TaxSubtotals.Select(s => (s.TaxCode, s.TaxableAmount, s.TaxAmount)).ToArray());
    }

    [Fact]
    public void Free_operations_are_valued_by_reference_but_never_payable()
    {
        var line = new TaxableLine(2, 0m, "11", ReferenceUnitValue: 10m);

        var r = Ok(Single(line));

        Assert.Equal(20m, r.TotalFree);
        Assert.Equal(20m, r.Lines[0].LineExtensionAmount);
        Assert.Equal(3.60m, r.Lines[0].IgvOrIvapAmount); // informational IGV of a taxed free transfer
        Assert.Null(r.Lines[0].UnitPriceIncludingTaxes);
        Assert.Equal(0m, r.TotalTaxAmount);
        Assert.Equal(0m, r.TotalLineExtensionAmount);
        Assert.Equal(0m, r.PayableAmount);
        var free = Assert.Single(r.TaxSubtotals);
        Assert.Equal(("9996", 20m, 3.60m), (free.TaxCode, free.TaxableAmount, free.TaxAmount));
    }

    [Theory]
    [InlineData("21")]
    [InlineData("31")]
    [InlineData("37")]
    public void Free_exempt_and_unaffected_transfers_carry_no_tax(string code)
    {
        var r = Ok(Single(new TaxableLine(1, 0m, code, ReferenceUnitValue: 5m)));

        Assert.Equal(0m, r.Lines[0].IgvOrIvapAmount);
        Assert.Equal(5m, r.TotalFree);
    }

    [Fact]
    public void Discount_affecting_the_base_reduces_value_and_tax()
    {
        var r = Ok(Single(new TaxableLine(1, 100m, "10", DiscountAffectingBase: 10m)));

        Assert.Equal(90m, r.Lines[0].LineExtensionAmount);
        Assert.Equal(16.20m, r.TotalIgv);
        Assert.Equal(106.20m, r.PayableAmount);
    }

    [Fact]
    public void Charge_affecting_the_base_increases_value_and_tax()
    {
        var r = Ok(Single(new TaxableLine(1, 100m, "10", ChargeAffectingBase: 10m)));

        Assert.Equal(110m, r.Lines[0].LineExtensionAmount);
        Assert.Equal(19.80m, r.TotalIgv);
    }

    [Fact]
    public void Non_affecting_charge_and_discount_only_move_the_payable_amount()
    {
        var charged = Ok(Single(new TaxableLine(1, 100m, "10", ChargeNotAffectingBase: 5m)));
        var discounted = Ok(Single(new TaxableLine(1, 100m, "10", DiscountNotAffectingBase: 10m)));

        Assert.Equal(123m, charged.PayableAmount);
        Assert.Equal(5m, charged.TotalCharges);
        Assert.Equal(123.0000000000m, charged.Lines[0].UnitPriceIncludingTaxes);
        Assert.Equal(18m, charged.TotalIgv);

        Assert.Equal(108m, discounted.PayableAmount);
        Assert.Equal(10m, discounted.TotalAllowances);
        Assert.Equal(18m, discounted.TotalIgv);
    }

    [Fact]
    public void Ad_valorem_isc_enters_the_igv_base()
    {
        var r = Ok(Single(new TaxableLine(1, 100m, "10", Isc: new IscInput(IscSystem.AdValorem, 0.10m))));

        Assert.Equal(10m, r.TotalIsc);
        Assert.Equal(19.80m, r.TotalIgv); // 18 % of (100 + 10)
        Assert.Equal(29.80m, r.TotalTaxAmount);
        Assert.Equal(129.80m, r.PayableAmount);
        Assert.Contains(r.TaxSubtotals, s => s.TaxCode == "2000" && s.TaxableAmount == 100m && s.TaxAmount == 10m);
    }

    [Fact]
    public void Fixed_amount_isc_is_per_unit()
    {
        var r = Ok(Single(new TaxableLine(3, 100m, "10", Isc: new IscInput(IscSystem.FixedAmount, 1.50m))));

        Assert.Equal(4.50m, r.TotalIsc);
        Assert.Equal(54.81m, r.TotalIgv); // 18 % of (300 + 4.50)
        Assert.Equal(359.31m, r.PayableAmount);
    }

    [Fact]
    public void Plastic_bag_tax_is_outside_the_igv_base_but_inside_the_total()
    {
        var r = Ok(Single(new TaxableLine(1, 10m, "10", PlasticBagCount: 3)));

        Assert.Equal(1.50m, r.TotalIcbper);
        Assert.Equal(1.80m, r.TotalIgv);
        Assert.Equal(3.30m, r.TotalTaxAmount);
        Assert.Equal(13.30m, r.PayableAmount);
        Assert.Contains(r.TaxSubtotals, s => s.TaxCode == "7152" && s.TaxAmount == 1.50m);
    }

    [Fact]
    public void Global_adjustments_that_affect_the_base_change_value_and_igv()
    {
        var lines = new[] { Taxed(1, 100m), Taxed(1, 100m) };

        var discounted = Ok(new TaxCalculationRequest(lines, Rates, new GlobalAdjustments(DiscountAffectingBase: 20m)));
        var charged = Ok(new TaxCalculationRequest(lines, Rates, new GlobalAdjustments(ChargeAffectingBase: 10m)));

        Assert.Equal(180m, discounted.TotalTaxableGravado);
        Assert.Equal(32.40m, discounted.TotalIgv);
        Assert.Equal(212.40m, discounted.PayableAmount);
        Assert.Equal(210m, charged.TotalTaxableGravado);
        Assert.Equal(37.80m, charged.TotalIgv);
        Assert.Equal(247.80m, charged.PayableAmount);
    }

    [Fact]
    public void Global_adjustments_that_do_not_affect_the_base_only_move_the_payable_amount()
    {
        var r = Ok(Single(Taxed(1, 100m), new GlobalAdjustments(DiscountNotAffectingBase: 5m, ChargeNotAffectingBase: 2m)));

        Assert.Equal(118m, r.TotalTaxInclusiveAmount);
        Assert.Equal(115m, r.PayableAmount);
        Assert.Equal(5m, r.TotalAllowances);
        Assert.Equal(2m, r.TotalCharges);
    }

    [Fact]
    public void Payable_rounding_is_added_to_the_total()
    {
        var r = Ok(Single(Taxed(1, 99.99m), new GlobalAdjustments(PayableRoundingAmount: 0.03m)));

        // 99.99 + 18 % (18.00 rounded) = 117.99 ; +0.03
        Assert.Equal(118.02m, r.PayableAmount);
        Assert.Equal(0.03m, r.PayableRoundingAmount);
    }

    [Fact]
    public void Special_igv_rate_is_just_another_input()
    {
        var r = Ok(Single(Taxed(1, 100m), rates: new TaxRates(0.105m)));

        Assert.Equal(10.50m, r.TotalIgv);
        Assert.Equal(110.50m, r.PayableAmount);
    }

    [Fact]
    public void Ivap_lines_use_the_ivap_rate_and_tax_code()
    {
        var r = Ok(Single(Taxed(1, 100m, "17")));

        Assert.Equal(4m, r.TotalIvap);
        Assert.Equal(0m, r.TotalIgv);
        Assert.Equal(104m, r.PayableAmount);
        Assert.Contains(r.TaxSubtotals, s => s.TaxCode == "1016" && s.TaxableAmount == 100m && s.TaxAmount == 4m);
    }

    [Fact]
    public void Calculation_is_deterministic_and_does_not_mutate_the_request()
    {
        var request = new TaxCalculationRequest([Taxed(7, 12.3456789012m), Taxed(1, 5m, "20")], Rates);

        var first = Ok(request);
        var second = Ok(request);

        Assert.Equal(first.PayableAmount, second.PayableAmount);
        Assert.Equal(first.Lines.Select(l => l.LineExtensionAmount), second.Lines.Select(l => l.LineExtensionAmount));
    }

    // ---------- validation ----------

    [Fact]
    public void Requests_without_lines_are_rejected() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(new TaxCalculationRequest([], Rates)));

    [Theory]
    [InlineData("99")]
    [InlineData("")]
    [InlineData("1")]
    public void Unknown_affectation_codes_are_rejected(string code) =>
        Assert.Equal(ErrorCodes.TaxUnsupported, ErrorOf(Single(Taxed(1, 10m, code))));

    [Fact]
    public void Quantity_must_be_positive() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(Taxed(0, 10m))));

    [Fact]
    public void Quantity_and_unit_value_allow_ten_decimals_but_not_eleven()
    {
        Ok(Single(Taxed(1.1234567891m, 1.1234567891m)));

        Assert.Equal(ErrorCodes.TaxPrecisionExceeded, ErrorOf(Single(Taxed(1.12345678911m, 1m))));
        Assert.Equal(ErrorCodes.TaxPrecisionExceeded, ErrorOf(Single(Taxed(1m, 1.12345678911m))));
    }

    [Fact]
    public void Trailing_zeros_do_not_count_as_precision() =>
        Ok(Single(Taxed(1.1000000000000m, 2.5000000000000m)));

    [Fact]
    public void Monetary_adjustments_allow_two_decimals_only()
    {
        Assert.Equal(ErrorCodes.TaxPrecisionExceeded, ErrorOf(Single(new TaxableLine(1, 100m, "10", DiscountAffectingBase: 1.005m))));
        Assert.Equal(ErrorCodes.TaxPrecisionExceeded, ErrorOf(Single(Taxed(1, 100m), new GlobalAdjustments(ChargeNotAffectingBase: 0.001m))));
    }

    [Fact]
    public void Negative_values_are_rejected()
    {
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(Taxed(1, -1m))));
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(new TaxableLine(1, 10m, "10", DiscountAffectingBase: -1m))));
    }

    [Fact]
    public void Free_operations_require_a_reference_value() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(new TaxableLine(1, 0m, "11"))));

    [Fact]
    public void Isc_is_only_allowed_on_igv_taxed_lines() =>
        Assert.Equal(ErrorCodes.TaxUnsupported, ErrorOf(Single(new TaxableLine(1, 10m, "20", Isc: new IscInput(IscSystem.AdValorem, 0.1m)))));

    [Fact]
    public void A_line_discount_cannot_exceed_the_line_value() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(new TaxableLine(1, 10m, "10", DiscountAffectingBase: 11m))));

    [Fact]
    public void A_global_discount_cannot_exceed_the_taxed_base() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(Taxed(1, 10m), new GlobalAdjustments(DiscountAffectingBase: 11m))));

    [Fact]
    public void Global_base_adjustments_need_a_taxed_base() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(Taxed(1, 10m, "20"), new GlobalAdjustments(DiscountAffectingBase: 1m))));

    [Fact]
    public void Rounding_beyond_one_is_rejected() =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(Taxed(1, 10m), new GlobalAdjustments(PayableRoundingAmount: 1.01m))));

    [Fact]
    public void Igv_and_ivap_cannot_be_mixed_yet() =>
        Assert.Equal(ErrorCodes.TaxUnsupported, ErrorOf(new TaxCalculationRequest([Taxed(1, 10m, "10"), Taxed(1, 10m, "17")], Rates)));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void Invalid_rates_are_rejected(double rate) =>
        Assert.Equal(ErrorCodes.TaxInvalidInput, ErrorOf(Single(Taxed(1, 10m), rates: new TaxRates((decimal)rate))));

    [Fact]
    public void Amounts_beyond_twelve_integer_digits_are_rejected()
    {
        Assert.Equal(ErrorCodes.TaxAmountOverflow, ErrorOf(Single(Taxed(999_999_999_999m, 999_999_999_999m))));
        Assert.Equal(ErrorCodes.TaxAmountOverflow, ErrorOf(Single(Taxed(decimal.MaxValue / 2, 10m))));
    }

    // ---------- invariants over random documents ----------

    [Fact]
    public void Random_documents_always_satisfy_the_official_consistency_rules()
    {
        var random = new Random(20260930);
        string[] codes = ["10", "10", "10", "20", "30", "40", "11", "21", "31"];

        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 8)).Select(_ =>
            {
                var code = codes[random.Next(codes.Length)];
                var quantity = Math.Round((decimal)random.NextDouble() * 50m + 0.001m, random.Next(0, 6));
                quantity = quantity == 0 ? 1 : quantity;
                var unit = Math.Round((decimal)random.NextDouble() * 500m, random.Next(0, 9));
                var gross = quantity * unit;
                var discount = random.Next(4) == 0 ? Math.Round(gross * (decimal)random.NextDouble() * 0.5m, 2) : 0m;
                var isFree = code is "11" or "21" or "31";
                return new TaxableLine(
                    quantity,
                    unit,
                    code,
                    DiscountAffectingBase: discount,
                    ChargeNotAffectingBase: random.Next(5) == 0 ? 1.25m : 0m,
                    ReferenceUnitValue: isFree ? Math.Max(unit, 0.01m) : null,
                    Isc: code == "10" && random.Next(6) == 0 ? new IscInput(IscSystem.AdValorem, 0.10m) : null,
                    PlasticBagCount: !isFree && random.Next(8) == 0 ? random.Next(1, 4) : 0);
            }).ToList();

            var result = _calculator.Calculate(new TaxCalculationRequest(lines, Rates));
            if (!result.IsSuccess)
            {
                Assert.Equal(ErrorCodes.TaxInvalidInput, result.Error.Code); // only "discount larger than value" may occur
                continue;
            }

            var r = result.Value;
            var payableLines = r.Lines.Where(l => l.TaxCode != TaxCodes.Free).ToList();

            Assert.Equal(payableLines.Sum(l => l.LineExtensionAmount), r.TotalLineExtensionAmount);
            Assert.Equal(r.TotalLineExtensionAmount + r.TotalTaxAmount, r.TotalTaxInclusiveAmount);
            Assert.Equal(r.TotalTaxInclusiveAmount + r.TotalCharges - r.TotalAllowances + r.PayableRoundingAmount, r.PayableAmount);
            Assert.Equal(r.TotalIgv + r.TotalIvap + r.TotalIsc + r.TotalIcbper, r.TotalTaxAmount);
            Assert.True(r.PayableAmount >= 0);

            // SUNAT compares sums with a tolerance of +-1; per-line rounding must never drift further than a cent per line.
            var lineIgv = payableLines.Where(l => l.TaxCode == TaxCodes.Igv).Sum(l => l.IgvOrIvapAmount);
            Assert.True(Math.Abs(r.TotalIgv - lineIgv) <= 0.01m * lines.Count, $"IGV drift {r.TotalIgv} vs {lineIgv}");

            Assert.All(r.Lines, l => Assert.True(Scale(l.LineExtensionAmount) <= 2));
            Assert.True(Scale(r.PayableAmount) <= 2);
        }
    }

    private static int Scale(decimal value) => (decimal.GetBits(value / 1.000000000000000000000000000000000m)[3] >> 16) & 0xFF;
}
