using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Application;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.TaxEngine;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Unit.Tests.CpeEngine;

public class QrPayloadGeneratorTests
{
    private const string Digest = "dGVzdC1kaWdlc3QtdmFsdWU="; // base64 of "test-digest-value"
    private readonly QrPayloadGenerator _generator = new();

    private static QrData Sample() => new(
        "20100066603", "01", "F001", "123", 18.00m, 118.00m, new DateOnly(2026, 9, 30), "6", "20100066603", Digest);

    [Fact]
    public void Fields_follow_the_order_and_separator_of_annex_6()
    {
        var result = _generator.Build(Sample());

        Assert.True(result.IsSuccess);
        Assert.Equal($"20100066603|01|F001|123|18.00|118.00|2026-09-30|6|20100066603|{Digest}", result.Value);
        Assert.Equal(10, result.Value.Split('|').Length);
    }

    [Fact]
    public void Amounts_always_use_two_decimals_and_a_dot_whatever_the_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-PE");
        try
        {
            var result = _generator.Build(Sample() with { TotalIgv = 0m, TotalAmount = 1234.5m });

            Assert.Contains("|0.00|1234.50|", result.Value, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Buyer_fields_stay_empty_when_the_document_has_no_buyer_identification()
    {
        var result = _generator.Build(Sample() with { BuyerDocumentTypeCode = null, BuyerDocumentNumber = null });

        Assert.EndsWith($"|2026-09-30|||{Digest}", result.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("F0|01")]
    [InlineData("F0\n01")]
    public void Values_that_could_split_or_forge_fields_are_rejected(string series)
    {
        var result = _generator.Build(Sample() with { Series = series });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.CpeInvalidQrField, result.Error.Code);
    }

    [Fact]
    public void Invalid_ruc_digest_and_amounts_are_rejected()
    {
        Assert.False(_generator.Build(Sample() with { IssuerRuc = "20100066604" }).IsSuccess);
        Assert.False(_generator.Build(Sample() with { DigestValue = "not base64!" }).IsSuccess);
        Assert.False(_generator.Build(Sample() with { DigestValue = "" }).IsSuccess);
        Assert.False(_generator.Build(Sample() with { TotalAmount = -1m }).IsSuccess);
        Assert.False(_generator.Build(Sample() with { Series = "" }).IsSuccess);
    }

    // ---------- what the document gives the QR ----------

    private static DocumentDto Issued(string affectation, string buyerType = "6", string buyerNumber = "20100070970")
    {
        var totals = new TaxCalculator().Calculate(new TaxCalculationRequest([new TaxableLine(1m, 100m, affectation)], new TaxRates(0.18m, 0.04m))).Value;
        return new DocumentDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "01", "F001", 123, new DateOnly(2026, 9, 30), "PEN", new BuyerSnapshot(buyerType, buyerNumber, "CLIENTE"),
            DocumentStatus.Validated, [], totals, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("10", "18.00", "118.00")]  // IGV: the sum of the IGV
    [InlineData("17", "0.00", "104.00")]   // IVAP: another tax, no IGV (S19 6.4.3 d names only the IGV)
    [InlineData("20", "0.00", "100.00")]   // exempt: no tax
    [InlineData("40", "0.00", "100.00")]   // export: no tax
    public void The_igv_field_carries_only_the_igv_and_the_total_is_the_payable_amount(string affectation, string igv, string total)
    {
        var result = _generator.Build(QrFields.From(Issued(affectation), "20100066603", Digest));

        Assert.True(result.IsSuccess);
        Assert.Equal($"20100066603|01|F001|123|{igv}|{total}|2026-09-30|6|20100070970|{Digest}", result.Value);
    }

    [Fact]
    public void A_buyer_without_document_leaves_its_fields_empty()
    {
        var result = _generator.Build(QrFields.From(Issued("10", "0", "-"), "20100066603", Digest));

        Assert.Equal($"20100066603|01|F001|123|18.00|118.00|2026-09-30|||{Digest}", result.Value);
    }
}
