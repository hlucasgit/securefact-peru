using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.CpeEngine;

/// <summary>
/// In-process simulator of SUNAT's bill service for development, demos and end-to-end tests (ADR-039). It never leaves the machine and it does <b>not</b> validate the document against
/// SUNAT's rules: it accepts what it receives, unless the document carries a marker in a line description. <c>[sandbox:rechazar]</c> answers a rejection as a fault (code 2800, the
/// document fails), <c>[sandbox:rechazar-cdr]</c> a CDR with the rejection code 2800 (the document is rejected) and <c>[sandbox:observar]</c> an acceptance with the observation 4030. The ticket of a summary or a voiding carries its own reference, so the simulator keeps no state and the API and the
/// workers can answer for each other.
/// </summary>
internal sealed partial class SandboxSunatChannel(ICpePackager packager, TimeProvider clock) : ICpeSubmissionChannel
{
    public const string RejectMarker = "[sandbox:rechazar]";
    public const string RejectCdrMarker = "[sandbox:rechazar-cdr]";
    public const string ObserveMarker = "[sandbox:observar]";
    private const string TicketPrefix = "SBX.";
    private const string SunatRuc = "20131312955";

    [GeneratedRegex(@"^(?<ruc>\d{11})-(?<type>01|03|07|08)-(?<id>[A-Z0-9]{4}-\d{1,8})\.zip$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BillFileName();

    [GeneratedRegex(@"^(?<ruc>\d{11})-(?<id>(?:RC|RA)-\d{8}-\d{1,5})\.zip$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SummaryFileName();

    public Task<ChannelReply> SendBillAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default)
    {
        var match = BillFileName().Match(zipFileName ?? string.Empty);
        if (!match.Success)
        {
            return Task.FromResult(ClientFault(151, "El nombre del archivo ZIP no tiene el formato RUC-TIPO-SERIE-NUMERO.zip."));
        }

        var content = packager.Unzip(zip);
        if (!content.IsSuccess)
        {
            return Task.FromResult(ClientFault(153, "El archivo ZIP no se pudo abrir."));
        }

        var id = match.Groups["id"].Value;
        var ruc = match.Groups["ruc"].Value;
        if (content.Value.Content.Contains(RejectMarker, StringComparison.Ordinal))
        {
            return Task.FromResult(ClientFault(2800, $"[Simulador] Rechazo simulado de {id} por la marca {RejectMarker}."));
        }

        var name = match.Groups["type"].Value switch { "01" => "La Factura", "03" => "La Boleta", "07" => "La Nota de Credito", _ => "La Nota de Debito" };
        if (content.Value.Content.Contains(RejectCdrMarker, StringComparison.Ordinal))
        {
            return Task.FromResult(ChannelReply.Cdr(CdrZip(ruc, id, $"{name} numero {id}, ha sido rechazada [Simulador: marca {RejectCdrMarker}]", code: 2800)));
        }

        var notes = content.Value.Content.Contains(ObserveMarker, StringComparison.Ordinal)
            ? new[] { $"4030 - [Simulador] Observación simulada de {id} por la marca {ObserveMarker}." }
            : [];
        return Task.FromResult(ChannelReply.Cdr(CdrZip(ruc, id, $"{name} numero {id}, ha sido aceptada", notes: notes)));
    }

    public Task<ChannelReply> SendSummaryAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default)
    {
        var match = SummaryFileName().Match(zipFileName ?? string.Empty);
        if (!match.Success)
        {
            return Task.FromResult(ClientFault(151, "El nombre del archivo ZIP no tiene el formato RUC-RC-FECHA-CORRELATIVO.zip."));
        }

        var reference = match.Groups["ruc"].Value + "/" + match.Groups["id"].Value;
        return Task.FromResult(ChannelReply.Issued(TicketPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(reference)).Replace('+', '-').Replace('/', '_').TrimEnd('=')));
    }

    public Task<ChannelReply> GetStatusAsync(SunatCredentials credentials, string ticket, CancellationToken cancellationToken = default)
    {
        if (!ticket.StartsWith(TicketPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(ClientFault(1033, "El ticket no fue emitido por el simulador."));
        }

        try
        {
            var encoded = ticket[TicketPrefix.Length..].Replace('-', '+').Replace('_', '/');
            var reference = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=')));
            var parts = reference.Split('/');
            var kind = parts[1].StartsWith("RA", StringComparison.Ordinal) ? "La Comunicacion de baja" : "El Resumen diario";
            return Task.FromResult(ChannelReply.Cdr(CdrZip(parts[0], parts[1], $"{kind} {parts[1]}, ha sido aceptado")));
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            return Task.FromResult(ClientFault(1033, "El ticket del simulador no es válido."));
        }
    }

    private static ChannelReply ClientFault(int code, string message) => ChannelReply.Failed(new SunatFault(SunatSide.Client, code, message, Retryable: false));

    /// <summary>A CDR ZIP with the structure of the Programmer Manual's examples (Annex 1), with the reference of the document it answers.</summary>
    private byte[] CdrZip(string taxpayerRuc, string reference, string description, int code = 0, params string[] notes)
    {
        var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-5));
        var xml =
            "<?xml version=\"1.0\" encoding=\"ISO-8859-1\" standalone=\"no\"?>" +
            "<ar:ApplicationResponse xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" " +
            "xmlns:ar=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" " +
            "xmlns:cac=\"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2\" " +
            "xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\" " +
            "xmlns:ext=\"urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2\">" +
            "<ext:UBLExtensions><ext:UBLExtension><ext:ExtensionContent/></ext:UBLExtension></ext:UBLExtensions>" +
            "<cbc:UBLVersionID>2.0</cbc:UBLVersionID><cbc:CustomizationID>1.0</cbc:CustomizationID>" +
            $"<cbc:ID>{now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}</cbc:ID>" +
            $"<cbc:IssueDate>{now:yyyy-MM-dd}</cbc:IssueDate><cbc:IssueTime>{now:HH:mm:ss}</cbc:IssueTime>" +
            $"<cbc:ResponseDate>{now:yyyy-MM-dd}</cbc:ResponseDate><cbc:ResponseTime>{now:HH:mm:ss}</cbc:ResponseTime>" +
            string.Concat(notes.Select(note => $"<cbc:Note>{SecurityElement.Escape(note)}</cbc:Note>")) +
            $"<cac:SenderParty><cac:PartyIdentification><cbc:ID>{SunatRuc}</cbc:ID></cac:PartyIdentification></cac:SenderParty>" +
            $"<cac:ReceiverParty><cac:PartyIdentification><cbc:ID>{taxpayerRuc}</cbc:ID></cac:PartyIdentification></cac:ReceiverParty>" +
            "<cac:DocumentResponse><cac:Response>" +
            $"<cbc:ReferenceID>{reference}</cbc:ReferenceID><cbc:ResponseCode>{code.ToString(CultureInfo.InvariantCulture)}</cbc:ResponseCode><cbc:Description>{SecurityElement.Escape(description)}</cbc:Description>" +
            "</cac:Response></cac:DocumentResponse></ar:ApplicationResponse>";
        return packager.Zip("R-" + reference, xml).Value;
    }
}
