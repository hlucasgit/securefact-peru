using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;

namespace SecureFact.Unit.Tests.Gre;

public class GreValidatorTests
{
    private static IReadOnlyList<string> Check(CreateGreRequest request, int maxLagDays = 1) => GreValidator.Validate(request, GreSamples.Context(maxLagDays));

    private static void AssertRule(string rule, IReadOnlyList<string> issues) =>
        Assert.Contains(issues, issue => issue.StartsWith($"[{rule}]", StringComparison.Ordinal));

    [Fact]
    public void A_sale_in_private_transport_and_the_same_in_public_transport_are_valid()
    {
        Assert.Empty(Check(GreSamples.PrivateSale()));
        Assert.Empty(Check(GreSamples.PublicSale()));
    }

    // ---------- header ----------

    [Fact]
    public void The_issue_date_is_not_in_the_future_and_not_older_than_the_parameter_allows()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("2329", Check(sale with { IssueDate = GreSamples.Today.AddDays(1) }));
        AssertRule("2108", Check(sale with { IssueDate = GreSamples.Today.AddDays(-2), TransferStartDate = GreSamples.Today }));
        Assert.DoesNotContain(Check(sale with { IssueDate = GreSamples.Today.AddDays(-2) }, maxLagDays: 3), i => i.StartsWith("[2108]", StringComparison.Ordinal));
        Assert.Empty(Check(sale with { IssueDate = null }));
    }

    [Fact]
    public void The_note_takes_no_line_breaks_and_at_most_250_characters()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("4186", Check(sale with { Note = "dos\nlíneas" }));
        AssertRule("4186", Check(sale with { Note = new string('x', 251) }));
        Assert.Empty(Check(sale with { Note = new string('x', 250) }));
    }

    [Fact]
    public void An_unknown_motive_or_modality_is_refused()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("3405", Check(sale with { MotiveCode = "99" }));
        AssertRule("2773", Check(sale with { ModalityCode = "03" }));
    }

    [Fact]
    public void The_motive_others_needs_a_real_description_and_no_other_motive_takes_one()
    {
        var other = GreSamples.PrivateSale() with { MotiveCode = "13", Buyer = null };

        AssertRule("3457", Check(other));
        AssertRule("4190", Check(other with { MotiveDescription = "ab" }));
        AssertRule("4190", Check(other with { MotiveDescription = "1234" }));
        AssertRule("4190", Check(other with { MotiveDescription = "Traslado" }));
        AssertRule("4190", Check(other with { MotiveDescription = "  VENTA. " }));
        Assert.Empty(Check(other with { MotiveDescription = "Préstamo de equipos para una feria" }));
        AssertRule("3457", Check(GreSamples.PrivateSale() with { MotiveDescription = "algo" }));
    }

    // ---------- parties ----------

    [Fact]
    public void The_recipient_is_not_the_sender_in_a_sale_and_is_the_sender_in_a_transfer_between_establishments()
    {
        var sale = GreSamples.PrivateSale();
        var itself = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC");

        AssertRule("2555", Check(sale with { Recipient = itself }));

        var transfer = sale with
        {
            MotiveCode = "04",
            Recipient = itself,
            RelatedDocuments = null,
            Origin = new GreAddressInput("150101", "Av. Argentina 123", GreSamples.SenderRuc, "0000"),
            Destination = new GreAddressInput("150122", "Calle Los Pinos 456", GreSamples.SenderRuc, "0001"),
        };
        Assert.Empty(Check(transfer));
        AssertRule("2554", Check(transfer with { Recipient = new GrePartyInput("6", GreSamples.CustomerRuc, "OTRA EMPRESA SAC") }));
    }

    [Fact]
    public void The_transfer_between_establishments_needs_the_code_and_the_ruc_of_the_sender_on_both_points()
    {
        var transfer = GreSamples.PrivateSale() with
        {
            MotiveCode = "04",
            Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC"),
            RelatedDocuments = null,
            Origin = new GreAddressInput("150101", "Av. Argentina 123"),
            Destination = new GreAddressInput("150122", "Calle Los Pinos 456", GreSamples.CustomerRuc, "0001"),
        };

        var issues = Check(transfer);

        AssertRule("3365", issues);
        AssertRule("3414", issues);
    }

    [Fact]
    public void A_return_and_a_transformation_need_a_ruc_recipient()
    {
        var sale = GreSamples.PrivateSale();
        var person = new GrePartyInput("1", "12345678", "JUAN PEREZ");

        AssertRule("3417", Check(sale with { MotiveCode = "06", Recipient = person, RelatedDocuments = null }));
        AssertRule("3417", Check(sale with { MotiveCode = "17", Recipient = person, RelatedDocuments = null }));
        Assert.Empty(Check(sale with { Recipient = person }));
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("6", "20123456789")]
    [InlineData("6", "123")]
    [InlineData("1", "1234")]
    [InlineData("1", "ABCDEFGH")]
    [InlineData("7", "con espacio")]
    [InlineData("9", "123")]
    public void A_document_without_the_shape_of_its_type_is_refused(string type, string number)
    {
        var issues = Check(GreSamples.PrivateSale() with { Recipient = new GrePartyInput(type, number, "NOMBRE") });

        Assert.Contains(issues, i => i.StartsWith("[2758]", StringComparison.Ordinal) || i.StartsWith("[2757]", StringComparison.Ordinal));
    }

    [Fact]
    public void The_supplier_is_for_purchases_and_collection_of_transformed_goods_and_the_buyer_for_sales_to_third_parties()
    {
        var sale = GreSamples.PrivateSale();
        var supplier = new GrePartyInput("6", GreSamples.CarrierRuc, "PROVEEDOR SAC");
        var buyer = new GrePartyInput("6", GreSamples.CarrierRuc, "COMPRADOR SAC");

        AssertRule("4054", Check(sale with { Supplier = supplier }));
        AssertRule("4377", Check(sale with { Buyer = buyer }));

        var purchase = sale with
        {
            MotiveCode = "02",
            Recipient = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC"),
            Supplier = supplier,
            RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-9", GreSamples.CarrierRuc)],
        };
        Assert.Empty(Check(purchase));
        AssertRule("3447", Check(purchase with { Supplier = supplier with { DocumentTypeCode = "0", DocumentNumber = "S/D" } }));
        AssertRule("3448", Check(purchase with { Supplier = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC") }));

        var third = sale with { MotiveCode = "03", Buyer = buyer };
        Assert.Empty(Check(third));
        AssertRule("4378", Check(third with { Buyer = null }));
        AssertRule("3335", Check(third with { Buyer = new GrePartyInput("6", GreSamples.CustomerRuc, "CLIENTE DEMO SAC") }));
        AssertRule("3334", Check(third with { Buyer = new GrePartyInput("6", GreSamples.SenderRuc, "EMISORA DEMO SAC") }));
    }

    // ---------- shipment ----------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000_000_000)]
    public void The_gross_weight_is_positive_and_fits_the_format(double weight)
    {
        AssertRule("2523", Check(GreSamples.PrivateSale() with { GrossWeight = (decimal)weight }));
    }

    [Fact]
    public void The_weight_takes_three_decimals_and_kilograms_or_tons()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("2523", Check(sale with { GrossWeight = 1.2345m }));
        AssertRule("2523", Check(sale with { WeightUnit = "LBR" }));
        Assert.Empty(Check(sale with { GrossWeight = 1.234m, WeightUnit = "TNE" }));
    }

    [Fact]
    public void Private_transport_needs_the_start_date_and_public_transport_the_handover_date()
    {
        var privateSale = GreSamples.PrivateSale();
        var publicSale = GreSamples.PublicSale();

        AssertRule("3406", Check(privateSale with { TransferStartDate = null }));
        AssertRule("3343", Check(privateSale with { TransferStartDate = GreSamples.Today.AddDays(-1) }));
        AssertRule("3617", Check(privateSale with { HandoverDate = GreSamples.Today }));
        AssertRule("3617", Check(publicSale with { HandoverDate = null }));
        AssertRule("3618", Check(publicSale with { HandoverDate = GreSamples.Today.AddDays(-1) }));
        AssertRule("3406", Check(publicSale with { TransferStartDate = GreSamples.Today }));
    }

    // ---------- transport ----------

    [Fact]
    public void Private_transport_needs_a_vehicle_and_a_driver_unless_the_vehicle_is_of_category_m1_or_l()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("2566", Check(sale with { Vehicle = null }));
        AssertRule("3357", Check(sale with { Driver = null }));
        Assert.Empty(Check(sale with { VehicleCategoryM1OrL = true, Vehicle = null, Driver = null }));
        AssertRule("3452", Check(sale with { VehicleCategoryM1OrL = true }));
    }

    [Fact]
    public void Public_transport_needs_the_carrier_and_does_not_take_vehicle_or_driver()
    {
        var sale = GreSamples.PublicSale();

        AssertRule("2558", Check(sale with { Carrier = null }));
        AssertRule("2485", Check(sale with { Carrier = new GreCarrierInput("20123456789", "TRANSPORTES SAC") }));
        AssertRule("2563", Check(sale with { Carrier = new GreCarrierInput(GreSamples.CarrierRuc, "ab") }));
        AssertRule("4392", Check(sale with { Carrier = new GreCarrierInput(GreSamples.CarrierRuc, "TRANSPORTES SAC", "con espacios") }));
        AssertRule("3354", Check(sale with { Vehicle = new GreVehicleInput("ABC123") }));
        AssertRule("3354", Check(sale with { Driver = GreSamples.PrivateSale().Driver }));
        AssertRule("3347", Check(GreSamples.PrivateSale() with { Carrier = new GreCarrierInput(GreSamples.CarrierRuc, "TRANSPORTES SAC") }));
    }

    [Theory]
    [InlineData("ABC-123")]
    [InlineData("abc")]
    [InlineData("000000")]
    [InlineData("ABCDEFGHI")]
    public void A_plate_has_six_to_eight_uppercase_letters_and_digits_and_not_only_zeros(string plate)
    {
        AssertRule("2567", Check(GreSamples.PrivateSale() with { Vehicle = new GreVehicleInput(plate) }));
    }

    [Fact]
    public void The_plate_is_taken_in_lower_case_too_because_it_is_written_in_upper_case()
    {
        Assert.Empty(Check(GreSamples.PrivateSale() with { Vehicle = new GreVehicleInput("abc123", "ab12345678") }));
    }

    [Fact]
    public void Up_to_two_secondary_vehicles_and_drivers_and_each_must_be_valid()
    {
        var sale = GreSamples.PrivateSale();
        var vehicles = new[] { new GreVehicleInput("DEF456"), new GreVehicleInput("GHI789") };
        var drivers = new[] { new GreDriverInput("1", "87654321", "ANA", "RUIZ", "Q87654321"), new GreDriverInput("1", "11223344", "LUIS", "SOTO", "Q11223344") };

        Assert.Empty(Check(sale with { SecondaryVehicles = vehicles, SecondaryDrivers = drivers }));
        AssertRule("4389", Check(sale with { SecondaryVehicles = [.. vehicles, new GreVehicleInput("JKL012")] }));
        AssertRule("3362", Check(sale with { SecondaryDrivers = [.. drivers, new GreDriverInput("1", "55667788", "EVA", "DIAZ", "Q55667788")] }));
        AssertRule("3362", Check(sale with { SecondaryDrivers = [drivers[0], drivers[0] with { DocumentNumber = "99999999" }] }));
        AssertRule("2567", Check(sale with { SecondaryVehicles = [new GreVehicleInput("x")] }));
    }

    [Fact]
    public void The_driver_is_not_identified_with_a_ruc_and_has_names_and_a_licence()
    {
        var sale = GreSamples.PrivateSale();
        var driver = sale.Driver!;

        AssertRule("2571", Check(sale with { Driver = driver with { DocumentTypeCode = "6", DocumentNumber = GreSamples.CustomerRuc } }));
        AssertRule("2571", Check(sale with { Driver = driver with { DocumentNumber = "1234" } }));
        AssertRule("3360", Check(sale with { Driver = driver with { FirstNames = "" } }));
        AssertRule("3360", Check(sale with { Driver = driver with { LastNames = " " } }));
        AssertRule("2573", Check(sale with { Driver = driver with { LicenseNumber = "123" } }));
        AssertRule("2573", Check(sale with { Driver = driver with { LicenseNumber = "000000000" } }));
        Assert.Empty(Check(sale with { Driver = driver with { DocumentTypeCode = "4", DocumentNumber = "CE1234567", LicenseNumber = "x12345678" } }));
    }

    // ---------- points ----------

    [Fact]
    public void A_point_has_a_six_digit_ubigeo_and_an_address()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("2776", Check(sale with { Origin = new GreAddressInput("1501", "Av. Argentina 123") }));
        AssertRule("2574", Check(sale with { Destination = new GreAddressInput("150122", "ab") }));
        AssertRule("2574", Check(sale with { Destination = new GreAddressInput("150122", "dos\nlíneas") }));
        AssertRule("2775", Check(sale with { Origin = null! }));
    }

    [Fact]
    public void An_establishment_comes_with_its_ruc_and_a_four_digit_code_or_not_at_all()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("3365", Check(sale with { Origin = new GreAddressInput("150101", "Av. Argentina 123", GreSamples.CustomerRuc, null) }));
        AssertRule("3410", Check(sale with { Origin = new GreAddressInput("150101", "Av. Argentina 123", null, "0001") }));
        AssertRule("3365", Check(sale with { Origin = new GreAddressInput("150101", "Av. Argentina 123", GreSamples.CustomerRuc, "1") }));
        AssertRule("3409", Check(sale with { Origin = new GreAddressInput("150101", "Av. Argentina 123", "20123456789", "0001") }));
        Assert.Empty(Check(sale with { Origin = new GreAddressInput("150101", "Av. Argentina 123", GreSamples.CustomerRuc, "0001") }));
    }

    [Fact]
    public void In_a_sale_the_arrival_establishment_is_not_the_senders_and_in_a_purchase_the_departure_one_is_not()
    {
        var sale = GreSamples.PrivateSale() with { Destination = new GreAddressInput("150122", "Calle Los Pinos 456", GreSamples.SenderRuc, "0001") };

        AssertRule("3411", Check(sale));
    }

    // ---------- goods ----------

    [Fact]
    public void There_is_at_least_one_good_and_each_one_is_described_measured_and_counted()
    {
        var sale = GreSamples.PrivateSale();
        var good = sale.Goods[0];

        AssertRule("2580", Check(sale with { Goods = [] }));
        AssertRule("2781", Check(sale with { Goods = [good with { Description = "ab" }] }));
        AssertRule("4320", Check(sale with { Goods = [good with { UnitCode = "XXX" }] }));
        AssertRule("2780", Check(sale with { Goods = [good with { Quantity = 0 }] }));
        AssertRule("2780", Check(sale with { Goods = [good with { Quantity = 1.12345678901m }] }));
        AssertRule("4085", Check(sale with { Goods = [good with { Code = new string('x', 31) }] }));
        AssertRule("3002", Check(sale with { Goods = [good with { SunatProductCode = "123456789" }] }));
        AssertRule("3002", Check(sale with { Goods = [good with { SunatProductCode = "00000000" }] }));
        AssertRule("3375", Check(sale with { Goods = [good with { Gtin = "123456789012345" }] }));
        Assert.Empty(Check(sale with { Goods = [good with { Quantity = 1.1234567890m, Code = null, SunatProductCode = null, Gtin = null }] }));
    }

    // ---------- related documents ----------

    [Fact]
    public void A_related_document_is_of_the_catalogue_of_the_sender_with_the_shape_of_its_number_and_its_issuer()
    {
        var sale = GreSamples.PrivateSale();

        AssertRule("2692", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("31", "V001-1", GreSamples.SenderRuc)] }));
        AssertRule("2692", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("99", "X", null)] }));
        AssertRule("3445", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("50", "118-2026-10-123456", null)] }));
        AssertRule("3403", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", " ", GreSamples.SenderRuc)] }));
        AssertRule("3441", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", "B001-1", GreSamples.SenderRuc)] }));
        AssertRule("3441", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-0", GreSamples.SenderRuc)] }));
        AssertRule("3380", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-1", null)] }));
        AssertRule("3409", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-1", "20123456789")] }));
        AssertRule("3381", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-1", GreSamples.CustomerRuc)] }));
        AssertRule("3340", Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("01", "F001-1", GreSamples.SenderRuc), new GreRelatedDocumentInput("01", "F001-1", GreSamples.SenderRuc)] }));
        Assert.Empty(Check(sale with { RelatedDocuments = null }));
        Assert.Empty(Check(sale with { RelatedDocuments = [new GreRelatedDocumentInput("49", "123456", null), new GreRelatedDocumentInput("12", "ABC-123", GreSamples.SenderRuc)] }));
    }

    [Fact]
    public void Several_problems_are_reported_together()
    {
        var issues = Check(GreSamples.PrivateSale() with { Goods = [], Vehicle = null, GrossWeight = 0 });

        Assert.True(issues.Count >= 3);
    }

    [Theory]
    [InlineData("hola", 1, 10, true)]
    [InlineData("  ", 1, 10, false)]
    [InlineData("a\tb", 1, 10, false)]
    [InlineData("a b", 1, 10, false)]
    [InlineData("áéíóú ñ", 1, 10, true)]
    [InlineData("12345678901", 1, 10, false)]
    public void Plain_text_is_text_with_spaces_and_no_other_whitespace(string value, int min, int max, bool expected) =>
        Assert.Equal(expected, GreValidator.IsPlainText(value, min, max));

    [Theory]
    [InlineData("  Venta.  ", "venta")]
    [InlineData("TRASLADO  ENTRE\tESTABLECIMIENTOS", "traslado entre establecimientos")]
    [InlineData("Devolución", "devolucion")]
    public void Descriptions_are_compared_without_case_accents_or_extra_spaces(string value, string expected) =>
        Assert.Equal(expected, GreValidator.Normalize(value));
}
