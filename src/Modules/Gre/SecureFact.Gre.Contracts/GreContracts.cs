using SecureFact.SharedKernel.Results;

namespace SecureFact.Gre.Contracts;

/// <summary>Where a guide is in its life (ADR-056). Accepted, accepted with observations and rejected are final; so is failed.</summary>
public enum GreState
{
    /// <summary>Numbered, validated and signed, not sent yet. It has no tax validity: the CDR does (R-010).</summary>
    Prepared,

    /// <summary>SUNAT took the file and gave a ticket; the answer is not there yet.</summary>
    Pending,

    /// <summary>CDR with response code 0: the guide is valid and the transfer may start.</summary>
    Accepted,

    /// <summary>CDR with response code 0 and notes: valid, and there is data that SUNAT may sanction.</summary>
    AcceptedWithObservations,

    /// <summary>CDR with a response code other than 0: no tax validity; a new guide must be issued.</summary>
    Rejected,

    /// <summary>The submission failed for good (the file did not pass the shape rules, or the attempts ran out) and there is no CDR.</summary>
    Failed,
}

/// <summary>A person or company that appears in the guide, identified with a catalogue 06 document.</summary>
public sealed record GrePartyInput(string DocumentTypeCode, string DocumentNumber, string Name);

/// <param name="UbigeoCode">Six digits of the INEI ubigeo (catalogue 13).</param>
/// <param name="EstablishmentRuc">RUC that the establishment belongs to; with <paramref name="EstablishmentCode"/> or neither.</param>
/// <param name="EstablishmentCode">Four-digit code of an annexed establishment («0000» is the main one).</param>
public sealed record GreAddressInput(string UbigeoCode, string Address, string? EstablishmentRuc = null, string? EstablishmentCode = null);

/// <param name="MtcRegistration">Registration of the carrier in the MTC; optional, up to 20 uppercase letters and digits.</param>
public sealed record GreCarrierInput(string Ruc, string Name, string? MtcRegistration = null);

/// <param name="CirculationCard">Electronic circulation card or vehicle enabling certificate; 10 to 15 uppercase letters and digits.</param>
public sealed record GreVehicleInput(string Plate, string? CirculationCard = null);

public sealed record GreDriverInput(string DocumentTypeCode, string DocumentNumber, string FirstNames, string LastNames, string LicenseNumber);

/// <param name="UnitCode">Catalogue 03 code (for example <c>NIU</c>, <c>ZZ</c>, <c>KGM</c>); catalogue 65 for the goods of a customs declaration.</param>
/// <param name="SunatProductCode">UNSPSC code of up to 8 digits (catalogue 25), optional.</param>
/// <param name="Customs">What a good says about its customs declaration or its manifest line; only for the motives 08, 09 and 19 (ADR-059).</param>
public sealed record GreGoodInput(string Description, string UnitCode, decimal Quantity, string? Code = null, string? SunatProductCode = null, string? Gtin = null, GreGoodCustomsInput? Customs = null);

/// <summary>
/// The properties of the catalogue 55 that a good carries in the guides with customs documents: the number of the declaration and the series of the good in it (export, 7021 and 7023), and the
/// line of the cargo manifest with its container (foreign goods, 7024 to 7028).
/// </summary>
/// <param name="DeclarationNumber">7021, as «118-2026-40-123456»: it is one of the related declarations (DAM or DS).</param>
/// <param name="DeclarationSeries">7023, the number of series of the good in the declaration, up to 4 digits.</param>
/// <param name="TransportDocument">7024, the transport document of the manifest, up to 25 capital letters, digits, «/», «\» and «-».</param>
/// <param name="TransportDetail">7025, the number of the detail of the transport document, up to 5 digits and more than zero.</param>
/// <param name="ManifestContainer">7026, the container of the line: one of the containers of the guide.</param>
/// <param name="Seal">7027, the seal of the container; required when the container is not empty.</param>
/// <param name="EmptyContainer">7028: true when the container travels empty.</param>
public sealed record GreGoodCustomsInput(
    string? DeclarationNumber = null,
    string? DeclarationSeries = null,
    string? TransportDocument = null,
    string? TransportDetail = null,
    string? ManifestContainer = null,
    string? Seal = null,
    bool? EmptyContainer = null);

/// <param name="Number">Up to 17 capital letters, digits, «-» and «/».</param>
/// <param name="Seal">Up to 100 capital letters and digits; several are separated by a comma.</param>
public sealed record GreContainerInput(string Number, string? Seal = null);

/// <summary>
/// What the guides of import (08), export (09) and foreign goods (19) add: the port or airport, whether the whole customs declaration travels, the net weight and its justification, and up to two
/// containers (ADR-059).
/// </summary>
/// <param name="PortCode">Catalogue 63 (port) or 64 (airport) code, according to <paramref name="PortType"/>.</param>
/// <param name="PortType"><c>1</c> port, <c>2</c> airport.</param>
/// <param name="PortName">Name of the port or airport, up to 200 characters.</param>
/// <param name="WholeTransfer">The whole declaration (DAM or DS) travels: the goods are not listed one by one.</param>
/// <param name="ManifestContainers">The goods are lines of a cargo manifest (91) that travel in containers.</param>
/// <param name="NetWeight">Net weight in kilograms of the goods of the declaration, up to 12 integers and 3 decimals.</param>
/// <param name="WeightNote">Why the gross weight of the load differs from the weight of the goods, up to 250 characters.</param>
public sealed record GreCustomsInput(
    string? PortCode = null,
    string? PortType = null,
    string? PortName = null,
    bool WholeTransfer = false,
    bool ManifestContainers = false,
    decimal? NetWeight = null,
    string? WeightNote = null,
    IReadOnlyList<GreContainerInput>? Containers = null);

/// <param name="TypeCode">Catalogue 61 code of a document that applies to the guide of the sender (for example <c>01</c> invoice, <c>03</c> receipt, <c>09</c> guide).</param>
/// <param name="IssuerRuc">RUC of the issuer of the document; required for invoices, receipts, purchase settlements, guides, operation vouchers and port orders.</param>
public sealed record GreRelatedDocumentInput(string TypeCode, string Number, string? IssuerRuc = null);

/// <summary>
/// Data of a guide of the sender (GRE remitente, type 09). The sender is the company. All the motives of the catalogue 20 except «otros» with a customs declaration (ADR-056, ADR-059): the guides of import
/// (08), export (09) and foreign goods (19) carry customs documents and <see cref="Customs"/>; the itinerant issuer (18) has no point of arrival.
/// </summary>
/// <param name="MotiveCode">Catalogue 20.</param>
/// <param name="MotiveDescription">Only for the motive 13 («otros»): what the transfer is, from 3 to 100 characters.</param>
/// <param name="Destination">The point of arrival; null only for the itinerant issuer (18), who does not know it.</param>
/// <param name="ModalityCode">Catalogue 18: 01 public transport, 02 private transport.</param>
/// <param name="TransferStartDate">Private transport: the day the transfer starts.</param>
/// <param name="HandoverDate">Public transport: the day the goods are handed to the carrier.</param>
/// <param name="GrossWeight">Gross weight of the load, positive, up to 12 integer digits and 3 decimals.</param>
/// <param name="WeightUnit"><c>KGM</c> or <c>TNE</c>.</param>
public sealed record CreateGreRequest(
    Guid CompanyId,
    Guid SeriesId,
    DateOnly? IssueDate,
    string MotiveCode,
    string? MotiveDescription,
    string ModalityCode,
    DateOnly? TransferStartDate,
    DateOnly? HandoverDate,
    decimal GrossWeight,
    string WeightUnit,
    int? PackageCount,
    string? Note,
    GrePartyInput Recipient,
    GrePartyInput? Supplier,
    GrePartyInput? Buyer,
    GreAddressInput Origin,
    GreAddressInput? Destination,
    GreCarrierInput? Carrier,
    GreVehicleInput? Vehicle,
    IReadOnlyList<GreVehicleInput>? SecondaryVehicles,
    GreDriverInput? Driver,
    IReadOnlyList<GreDriverInput>? SecondaryDrivers,
    IReadOnlyList<GreGoodInput> Goods,
    IReadOnlyList<GreRelatedDocumentInput>? RelatedDocuments = null,
    bool PlannedTransshipment = false,
    bool VehicleCategoryM1OrL = false,
    bool ReturnWithEmptyPackaging = false,
    bool ReturnEmptyVehicle = false,
    GreCustomsInput? Customs = null);

/// <summary>Who pays the freight of a guide of the carrier (the indicators <c>SUNAT_Envio_IndicadorPagadorFlete_*</c>, rule 4388).</summary>
public enum GreFreightPayer
{
    /// <summary>The sender of the goods.</summary>
    Sender,

    /// <summary>The company that the carrier subcontracted.</summary>
    Subcontractor,

    /// <summary>A third party, who then has to be named.</summary>
    ThirdParty,
}

/// <summary>
/// Data of a guide of the carrier (GRE transportista, type 31, series <c>V###</c>). The carrier is the company. It states who sends the goods, who receives them, the vehicle and the driver that
/// run the transfer, and either the goods or the guide of the sender (09) that already lists them (ADR-057).
/// </summary>
/// <param name="TransferStartDate">The day the transfer starts; not before the issue date.</param>
/// <param name="MtcRegistration">Registration of the carrier in the MTC; optional, up to 20 uppercase letters and digits.</param>
/// <param name="Sender">Who sends the goods (the shipper): not the carrier itself.</param>
/// <param name="Recipient">Who receives the goods.</param>
/// <param name="Vehicle">The principal vehicle, with its circulation card.</param>
/// <param name="Driver">The principal driver.</param>
/// <param name="Goods">The goods; none when a guide of the sender (09, series <c>T…</c>) is the related document, because that guide already lists them.</param>
/// <param name="RelatedDocuments">One related document, or several when one of them is a guide of the sender.</param>
/// <param name="WholeTransfer">The whole of the goods of the related voucher travel (<c>SUNAT_Envio_IndicadorTrasladoTotal</c>): the goods are not listed again. Needs an invoice, receipt, purchase settlement, ticket or voucher of Law 29972 as the related document (ADR-060).</param>
/// <param name="WholeTransferNote">The annotation about the goods that a receipt, a ticket, a voucher of Law 29972 or an invoice of numeric series needs with the whole transfer, since SUNAT has no list of their goods: from 3 to 500 characters.</param>
/// <param name="Subcontractor">The company that the carrier subcontracted; required when <paramref name="Subcontracted"/>.</param>
/// <param name="FreightPayer">Who pays the freight.</param>
/// <param name="ThirdPartyPayer">The third party that pays; required when <paramref name="FreightPayer"/> is <see cref="GreFreightPayer.ThirdParty"/>.</param>
public sealed record CreateGreCarrierRequest(
    Guid CompanyId,
    Guid SeriesId,
    DateOnly? IssueDate,
    DateOnly TransferStartDate,
    decimal GrossWeight,
    string WeightUnit,
    int? PackageCount,
    string? Note,
    string? MtcRegistration,
    GrePartyInput Sender,
    GrePartyInput Recipient,
    GreAddressInput Origin,
    GreAddressInput Destination,
    GreVehicleInput Vehicle,
    IReadOnlyList<GreVehicleInput>? SecondaryVehicles,
    GreDriverInput Driver,
    IReadOnlyList<GreDriverInput>? SecondaryDrivers,
    IReadOnlyList<GreGoodInput>? Goods,
    IReadOnlyList<GreRelatedDocumentInput>? RelatedDocuments,
    GreFreightPayer FreightPayer = GreFreightPayer.Sender,
    GrePartyInput? ThirdPartyPayer = null,
    bool Subcontracted = false,
    GrePartyInput? Subcontractor = null,
    bool PlannedTransshipment = false,
    bool ReturnWithEmptyPackaging = false,
    bool ReturnEmptyVehicle = false,
    bool WholeTransfer = false,
    string? WholeTransferNote = null);

public sealed record GreObservation(string Code, string Message);

public sealed record GreDto(
    Guid Id,
    Guid CompanyId,
    string Series,
    long Number,
    string Name,
    DateOnly IssueDate,
    string DocumentTypeCode,
    string? MotiveCode,
    string? ModalityCode,
    GreState State,
    string RecipientDocument,
    string RecipientName,
    string? Ticket,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? ProcessedAt,
    int? CdrResponseCode,
    string? CdrDescription,
    IReadOnlyList<GreObservation> Observations,
    string? ErrorCode,
    string? ErrorMessage);

/// <param name="DocumentTypeCode"><c>09</c> sender (series <c>T…</c>) or <c>31</c> carrier (series <c>V…</c>).</param>
public sealed record GreSeriesDto(Guid Id, Guid CompanyId, string DocumentTypeCode, string Code, long LastNumber, bool IsActive, DateTimeOffset CreatedAt);

public interface IGreSeriesAdministration
{
    /// <summary>A series of the guide of the sender starts with «T», one of the carrier with «V», and each has three more uppercase letters or digits (R-062, R-069).</summary>
    Task<Result<GreSeriesDto>> CreateAsync(Guid companyId, string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<GreSeriesDto>> ListAsync(Guid companyId, CancellationToken cancellationToken);

    Task<Result<Unit>> DeactivateAsync(Guid seriesId, CancellationToken cancellationToken);
}

public interface IGreService
{
    /// <summary>
    /// Checks the data against the rules of the official validation workbook, takes the next number of the series, builds the XML, signs it and stores it: the guide is <see cref="GreState.Prepared"/>.
    /// </summary>
    Task<Result<GreDto>> CreateAsync(CreateGreRequest request, CancellationToken cancellationToken);

    /// <summary>The same for a guide of the carrier (type 31, ADR-057).</summary>
    Task<Result<GreDto>> CreateCarrierAsync(CreateGreCarrierRequest request, CancellationToken cancellationToken);

    /// <summary>Sends a prepared guide to SUNAT. Without an answer yet the guide is <see cref="GreState.Pending"/> with its ticket.</summary>
    Task<Result<GreDto>> SubmitAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Asks SUNAT for the answer of a pending guide and applies it.</summary>
    Task<Result<GreDto>> RefreshAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<GreDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<GreDto>> ListAsync(Guid? companyId, GreState? state, int skip, int take, CancellationToken cancellationToken);

    /// <summary>The signed XML of the guide.</summary>
    Task<Result<string>> GetXmlAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The CDR zip that SUNAT answered with, when it did.</summary>
    Task<Result<byte[]>> GetCdrAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The printed representation (PDF) of the guide, of any state: a guide that SUNAT has not accepted carries a mark that says it does not support the transfer (ADR-058).</summary>
    Task<Result<byte[]>> GetPdfAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Sends the pending work of the guides (ADR-056): asks SUNAT for the tickets that are waiting. Platform scope, across accounts.</summary>
public interface IGreWorkProcessor
{
    /// <returns>How many guides changed state in this pass.</returns>
    Task<int> RunOnceAsync(CancellationToken cancellationToken);
}

/// <summary>What the REST channel needs to authenticate for a company. The secrets never appear in a log or in <see cref="ToString"/>.</summary>
public sealed record GreChannelCredentials(string Ruc, string SolUser, string SolPassword, string ClientId, string ClientSecret)
{
    public override string ToString() => $"GreChannelCredentials {{ Ruc = {Ruc}, SolUser = {SolUser}, SolPassword = *****, ClientId = {ClientId}, ClientSecret = ***** }}";
}

/// <param name="DocumentTypeCode"><c>09</c> sender, <c>31</c> carrier.</param>
/// <param name="FileBaseName"><c>RUC-09-T001-1</c>: the name of the XML and of the zip, without extension.</param>
public sealed record GreSubmission(GreChannelCredentials Credentials, string DocumentTypeCode, string Series, long Number, string FileBaseName, byte[] Zip);

public enum GreSubmitStatus
{
    /// <summary>SUNAT took the file and returned a ticket.</summary>
    Received,

    /// <summary>The call was refused for good: the shape of the request (501 to 507, 155 to 161), the authentication or a 422.</summary>
    Refused,

    /// <summary>SUNAT or the network could not answer now (5xx, 429, timeout); sending the same file again is safe.</summary>
    Transient,
}

public sealed record GreSubmitOutcome(GreSubmitStatus Status, string? Ticket, string? ErrorCode, string? ErrorMessage);

public enum GreTicketStatus
{
    /// <summary>Code 98: SUNAT is still processing it.</summary>
    InProcess,

    /// <summary>Code 0: processed; the CDR comes with it.</summary>
    Done,

    /// <summary>Code 99: processed with an error; the CDR comes with it when SUNAT generated one.</summary>
    Error,

    /// <summary>SUNAT or the network could not answer now; ask again later.</summary>
    Transient,
}

public sealed record GreTicketOutcome(GreTicketStatus Status, byte[]? CdrZip, string? ErrorCode, string? ErrorMessage);

/// <summary>The REST channel to SUNAT's GRE platform (S28, S29). Implementations: the real one, and the simulator for development and tests (ADR-039 and ADR-056).</summary>
public interface IGreChannel
{
    Task<GreSubmitOutcome> SubmitAsync(GreSubmission submission, CancellationToken cancellationToken);

    Task<GreTicketOutcome> QueryTicketAsync(GreChannelCredentials credentials, string ticket, CancellationToken cancellationToken);
}
