using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.Unit.Tests.CpeEngine;

public class CdrParserTests
{
    private readonly ZipCpePackager _packager = new();
    private readonly CdrParser _parser;

    public CdrParserTests() => _parser = new CdrParser(_packager);

    // Structure and values follow the examples in Annex 1 of the SUNAT Programmer Manual (accepted and rejected cases).
    private static string Cdr(string code = "0", string description = "La Factura numero FA01-981, ha sido aceptada", params string[] notes) =>
        "<?xml version=\"1.0\" encoding=\"ISO-8859-1\" standalone=\"no\"?>" +
        "<ar:ApplicationResponse xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" " +
        "xmlns:ar=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" " +
        "xmlns:cac=\"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2\" " +
        "xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\" " +
        "xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\" " +
        "xmlns:ext=\"urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2\">" +
        "<ext:UBLExtensions><ext:UBLExtension><ext:ExtensionContent/></ext:UBLExtension></ext:UBLExtensions>" +
        "<cbc:UBLVersionID>2.0</cbc:UBLVersionID><cbc:CustomizationID>1.0</cbc:CustomizationID>" +
        "<cbc:ID>201200000230061</cbc:ID><cbc:IssueDate>2012-06-12</cbc:IssueDate><cbc:IssueTime>10:09:27</cbc:IssueTime>" +
        "<cbc:ResponseDate>2012-06-12</cbc:ResponseDate><cbc:ResponseTime>10:09:30</cbc:ResponseTime>" +
        string.Concat(notes.Select(n => $"<cbc:Note>{n}</cbc:Note>")) +
        "<cac:SenderParty><cac:PartyIdentification><cbc:ID>20131312955</cbc:ID></cac:PartyIdentification></cac:SenderParty>" +
        "<cac:ReceiverParty><cac:PartyIdentification><cbc:ID>20150147718</cbc:ID></cac:PartyIdentification></cac:ReceiverParty>" +
        "<cac:DocumentResponse><cac:Response><cbc:ReferenceID>FA01-981</cbc:ReferenceID>" +
        $"<cbc:ResponseCode>{code}</cbc:ResponseCode><cbc:Description><![CDATA[{description}]]></cbc:Description></cac:Response>" +
        "<cac:DocumentReference><cbc:ID>FA01-981</cbc:ID></cac:DocumentReference>" +
        "<cac:RecipientParty><cac:PartyIdentification><cbc:ID>6-20997898754</cbc:ID></cac:PartyIdentification></cac:RecipientParty>" +
        "</cac:DocumentResponse></ar:ApplicationResponse>";

    [Fact]
    public void An_accepted_cdr_with_observations_is_read_completely()
    {
        var result = _parser.Parse(Cdr(notes: ["4031 - Debe indicar el nombre comercial", "4001 - El numero de RUC del receptor no existe."]));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        var cdr = result.Value;
        Assert.Equal("201200000230061", cdr.ProcessId);
        Assert.Equal(new DateOnly(2012, 6, 12), cdr.ReceivedDate);
        Assert.Equal(new TimeOnly(10, 9, 27), cdr.ReceivedTime);
        Assert.Equal(new TimeOnly(10, 9, 30), cdr.ResponseTime);
        Assert.Equal("20131312955", cdr.SunatRuc);
        Assert.Equal("20150147718", cdr.TaxpayerRuc);
        Assert.Equal("FA01-981", cdr.ReferenceId);
        Assert.Equal(0, cdr.ResponseCode);
        Assert.Equal("La Factura numero FA01-981, ha sido aceptada", cdr.Description);
        Assert.Equal(CdrStatus.AcceptedWithObservations, cdr.Status);
        Assert.Equal([new CdrObservation("4031", "Debe indicar el nombre comercial"), new CdrObservation("4001", "El numero de RUC del receptor no existe.")], cdr.Observations);
    }

    [Fact]
    public void An_accepted_cdr_without_notes_is_plain_accepted()
    {
        var cdr = _parser.Parse(Cdr()).Value;

        Assert.Equal(CdrStatus.Accepted, cdr.Status);
        Assert.Empty(cdr.Observations);
    }

    [Theory]
    [InlineData("2047")]
    [InlineData("3030")]
    [InlineData("1033")]
    [InlineData("4000")]
    public void Any_non_zero_response_code_is_a_rejection(string code)
    {
        var cdr = _parser.Parse(Cdr(code, "Es obligatorio al menos un AdditionalMonetaryTotal con codigo 1001, 1002 o 1003")).Value;

        Assert.Equal(CdrStatus.Rejected, cdr.Status);
        Assert.Equal(int.Parse(code, System.Globalization.CultureInfo.InvariantCulture), cdr.ResponseCode);
    }

    [Fact]
    public void A_note_without_a_code_is_kept_not_dropped()
    {
        var cdr = _parser.Parse(Cdr(notes: ["Mensaje libre"])).Value;

        Assert.Equal(CdrStatus.AcceptedWithObservations, cdr.Status);
        Assert.Equal(new CdrObservation(string.Empty, "Mensaje libre"), Assert.Single(cdr.Observations));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<not-xml")]
    [InlineData("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"/>")]
    public void Documents_that_are_not_a_cdr_are_rejected(string xml) =>
        Assert.False(_parser.Parse(xml).IsSuccess);

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("0 ")]
    public void A_response_code_that_is_not_a_plain_number_never_counts_as_accepted(string code)
    {
        // "0 " is trimmed by the parser (XML whitespace); the rest are invalid.
        var result = _parser.Parse(Cdr(code));

        Assert.True(code == "0 " ? result.IsSuccess && result.Value.Status == CdrStatus.Accepted : !result.IsSuccess);
    }

    [Fact]
    public void Missing_mandatory_data_is_rejected()
    {
        Assert.False(_parser.Parse(Cdr().Replace("<cbc:ResponseTime>10:09:30</cbc:ResponseTime>", string.Empty, StringComparison.Ordinal)).IsSuccess);
        Assert.False(_parser.Parse(Cdr().Replace("20131312955", string.Empty, StringComparison.Ordinal)).IsSuccess);
        Assert.False(_parser.Parse(Cdr().Replace("2012-06-12", "12/06/2012", StringComparison.Ordinal)).IsSuccess);
    }

    [Fact]
    public void Dtds_and_external_entities_are_never_processed()
    {
        var body = Cdr();
        var hostile = string.Concat("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>", body.AsSpan(body.IndexOf("<ar:", StringComparison.Ordinal)));

        Assert.False(_parser.Parse(hostile).IsSuccess);
    }

    [Fact]
    public void A_zipped_cdr_is_unzipped_and_parsed()
    {
        var zip = _packager.Zip("R-20150147718-01-FA01-981", Cdr()).Value;

        var result = _parser.ParseZip(zip);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        Assert.Equal("FA01-981", result.Value.ReferenceId);
        Assert.False(_parser.ParseZip([1, 2, 3]).IsSuccess);
    }

    [Theory]
    [InlineData(99, SunatCodeKind.Unclassified, false)]
    [InlineData(100, SunatCodeKind.SunatException, true)]
    [InlineData(999, SunatCodeKind.SunatException, true)]
    [InlineData(1000, SunatCodeKind.TaxpayerException, false)]
    [InlineData(1999, SunatCodeKind.TaxpayerException, false)]
    [InlineData(2000, SunatCodeKind.Rejection, false)]
    [InlineData(3999, SunatCodeKind.Rejection, false)]
    [InlineData(4000, SunatCodeKind.Observation, false)]
    [InlineData(0, SunatCodeKind.Unclassified, false)]
    public void Codes_are_classified_by_the_ranges_of_the_manual(int code, SunatCodeKind kind, bool retryable)
    {
        Assert.Equal(kind, SunatCodes.Classify(code));
        Assert.Equal(retryable, SunatCodes.IsRetryable(code));
    }
}
