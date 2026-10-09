using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;

namespace SecureFact.Unit.Tests.Gre;

/// <summary>Valid guides and the context they are checked in, for the tests of the validator and of the generator.</summary>
internal static class GreSamples
{
    public const string SenderRuc = "20100066603";
    public const string CustomerRuc = "20100070970";
    public const string CarrierRuc = "20601030013";

    public static readonly DateOnly Today = new(2026, 10, 8);

    public static GreValidationContext Context(int maxLagDays = 1) => new(
        SenderRuc,
        Today,
        maxLagDays,
        new HashSet<string>(["venta", "traslado", "otros", "varios"], StringComparer.Ordinal),
        new GreCatalogs(
            new HashSet<string>(["NIU", "ZZ", "KGM", "BX"], StringComparer.Ordinal),
            new HashSet<string>(["0", "1", "4", "6", "7", "A"], StringComparer.Ordinal),
            new HashSet<string>(["01", "02"], StringComparer.Ordinal),
            new HashSet<string>(["01", "02", "03", "04", "05", "06", "07", "08", "09", "13", "14", "17", "18", "19"], StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["01"] = "remitente, transportista",
                ["03"] = "remitente, transportista",
                ["04"] = "remitente, transportista",
                ["09"] = "remitente, transportista",
                ["12"] = "remitente, transportista",
                ["31"] = "solo transportista",
                ["48"] = "remitente, transportista",
                ["49"] = "solo remitente",
                ["50"] = "remitente, transportista",
                ["80"] = "remitente, transportista",
                ["91"] = "solo remitente",
                ["52"] = "solo remitente",
                ["92"] = "solo remitente",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["CLL"] = "070101", ["PAI"] = "200901" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AQP"] = "040104", ["LIM"] = "070101" },
            new HashSet<string>(["U", "2U", "KGM", "NIU"], StringComparer.Ordinal)));

    public static readonly IReadOnlyDictionary<string, string> DocumentNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "Factura",
        ["09"] = "Guía de remisión remitente",
    };

    /// <summary>A sale (01) in private transport (02) with its vehicle, its driver, one good and the invoice.</summary>
    public static CreateGreRequest PrivateSale() => new(
        CompanyId: Guid.Empty,
        SeriesId: Guid.Empty,
        IssueDate: Today,
        MotiveCode: "01",
        MotiveDescription: null,
        ModalityCode: "02",
        TransferStartDate: Today,
        HandoverDate: null,
        GrossWeight: 12.5m,
        WeightUnit: "KGM",
        PackageCount: 3,
        Note: "Entrega en almacén",
        Recipient: new GrePartyInput("6", CustomerRuc, "CLIENTE DEMO SAC"),
        Supplier: null,
        Buyer: null,
        Origin: new GreAddressInput("150101", "Av. Argentina 123, Lima"),
        Destination: new GreAddressInput("150122", "Calle Los Pinos 456, Miraflores"),
        Carrier: null,
        Vehicle: new GreVehicleInput("ABC123", "1234567890"),
        SecondaryVehicles: null,
        Driver: new GreDriverInput("1", "12345678", "JUAN CARLOS", "PEREZ GOMEZ", "Q12345678"),
        SecondaryDrivers: null,
        Goods: [new GreGoodInput("Caja de repuestos", "NIU", 3m, "REP-01", "27112100", "7750000000011")],
        RelatedDocuments: [new GreRelatedDocumentInput("01", "F001-123", SenderRuc)]);

    /// <summary>The same sale in public transport: a carrier and the day the goods are handed over, with no vehicle or driver of the sender.</summary>
    public static CreateGreRequest PublicSale() => PrivateSale() with
    {
        ModalityCode = "01",
        TransferStartDate = null,
        HandoverDate = Today,
        Carrier = new GreCarrierInput(CarrierRuc, "TRANSPORTES RAPIDOS SAC", "MTC123456"),
        Vehicle = null,
        Driver = null,
    };
}
