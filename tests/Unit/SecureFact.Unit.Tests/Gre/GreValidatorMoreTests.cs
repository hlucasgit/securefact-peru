using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;

namespace SecureFact.Unit.Tests.Gre;

/// <summary>The rules of the validator that the main tests do not reach: the missing recipient, the identity of the supplier, the issuers and numbers of the related documents, and the shape of the unit.</summary>
public class GreValidatorMoreTests
{
    private static IReadOnlyList<string> Check(CreateGreRequest request, GreValidationContext? context = null) => GreValidator.Validate(request, context ?? GreSamples.Context());

    private static void AssertRule(string rule, IReadOnlyList<string> issues) =>
        Assert.Contains(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    private static void AssertNoRule(string rule, IReadOnlyList<string> issues) =>
        Assert.DoesNotContain(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    [Fact]
    public void A_guide_without_a_recipient_is_refused()
    {
        AssertRule("2757", Check(GreSamples.PrivateSale() with { Recipient = null! }));
    }

    [Fact]
    public void A_party_without_a_name_is_refused()
    {
        var issues = Check(GreSamples.PrivateSale() with { Recipient = new GrePartyInput("6", GreSamples.CustomerRuc, "") });

        Assert.Contains(issues, issue => issue.Contains("razón social", StringComparison.Ordinal));
    }

    [Fact]
    public void The_supplier_of_a_pickup_of_transformed_goods_is_identified_by_RUC()
    {
        var sale = GreSamples.PrivateSale() with { MotiveCode = "07", Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "REMITENTE SAC"), Supplier = new GrePartyInput("1", "12345678", "PERSONA NATURAL") };

        AssertRule("3447", Check(sale));
    }

    [Fact]
    public void A_negative_package_count_is_refused()
    {
        AssertRule("3489", Check(GreSamples.PrivateSale() with { PackageCount = -1 }));
    }

    [Fact]
    public void The_circulation_card_has_10_to_15_capitals_and_digits()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("3355", Check(sale with { Vehicle = new GreVehicleInput("ABC123", "abc") }));
        AssertNoRule("3355", Check(sale with { Vehicle = new GreVehicleInput("ABC123", "1234567890") }));
    }

    [Fact]
    public void A_purchase_does_not_start_from_an_establishment_of_the_sender()
    {
        var sale = GreSamples.PrivateSale() with
        {
            MotiveCode = "02",
            Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "REMITENTE SAC"),
            Supplier = new GrePartyInput("6", GreSamples.CustomerRuc, "PROVEEDOR SAC"),
            Origin = new GreAddressInput("150101", "Av. Argentina 123, Lima", GreSamples.SenderRuc, "0001"),
        };

        AssertRule("3411", Check(sale));
    }

    [Fact]
    public void No_more_than_9999_goods_travel_in_one_guide()
    {
        var good = new GreGoodInput("Caja de repuestos", "NIU", 1m);
        var sale = GreSamples.PrivateSale() with { Goods = Enumerable.Repeat(good, 10_000).ToList() };

        AssertRule("2023", Check(sale));
    }

    [Fact]
    public void When_the_catalogue_of_units_is_not_a_list_only_the_shape_of_the_code_is_checked()
    {
        var context = GreSamples.Context();
        var open = context with { Catalogs = context.Catalogs with { UnitCodes = new HashSet<string>(StringComparer.Ordinal) } };
        var sale = GreSamples.PrivateSale();
        var good = sale.Goods[0];

        AssertRule("2883", Check(sale with { Goods = [good with { UnitCode = "TOOLONG" }] }, open));
        AssertRule("2883", Check(sale with { Goods = [good with { UnitCode = "" }] }, open));
        Assert.Empty(Check(sale with { Goods = [good with { UnitCode = "ZZ" }] }, open));
    }

    [Fact]
    public void The_issuer_of_a_related_guide_is_the_sender()
    {
        var sale = GreSamples.PrivateSale() with { RelatedDocuments = [new GreRelatedDocumentInput("09", "T001-5", GreSamples.CustomerRuc)] };

        AssertRule("3381", Check(sale));
    }

    [Fact]
    public void In_a_purchase_the_issuer_of_a_purchase_document_is_the_sender()
    {
        var sale = GreSamples.PrivateSale() with
        {
            MotiveCode = "02",
            Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "REMITENTE SAC"),
            Supplier = new GrePartyInput("6", GreSamples.CustomerRuc, "PROVEEDOR SAC"),
            RelatedDocuments = [new GreRelatedDocumentInput("04", "L001-5", GreSamples.CustomerRuc)],
        };

        AssertRule("3381", Check(sale));
    }

    [Fact]
    public void In_a_return_the_issuer_of_the_invoice_is_the_recipient()
    {
        var sale = GreSamples.PrivateSale() with { MotiveCode = "06", RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-5", GreSamples.SenderRuc)] };

        AssertRule("3381", Check(sale));
    }

    [Theory]
    [InlineData("03", "B001-5", true)]
    [InlineData("03", "X-1", false)]
    [InlineData("04", "L001-5", true)]
    [InlineData("04", "F001-5", false)]
    [InlineData("09", "T001-5", true)]
    [InlineData("09", "F001-5", false)]
    [InlineData("48", "1-5", true)]
    [InlineData("48", "ABC", false)]
    [InlineData("80", "123456", true)]
    [InlineData("80", "000", false)]
    public void The_number_of_a_related_document_has_the_shape_of_its_type(string type, string number, bool valid)
    {
        var sale = GreSamples.PrivateSale() with { RelatedDocuments = [new GreRelatedDocumentInput(type, number, GreSamples.SenderRuc)] };

        var issues = Check(sale);

        if (valid)
        {
            AssertNoRule("3441", issues);
        }
        else
        {
            AssertRule("3441", issues);
        }
    }
}
