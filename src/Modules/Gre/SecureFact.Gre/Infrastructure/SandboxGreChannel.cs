using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Gre.Contracts;

namespace SecureFact.Gre.Infrastructure;

/// <summary>
/// In-process simulator of SUNAT's GRE platform for development, demos and end-to-end tests (ADR-039, ADR-056). It never leaves the machine and it does <b>not</b> validate the guide
/// against SUNAT's rules, and above all not against the registers SUNAT keeps (carriers, vehicles, drivers): it accepts what it receives unless the XML carries a marker.
/// <c>[sandbox:rechazar]</c> refuses the send (error 502), <c>[sandbox:rechazar-cdr]</c> answers a CDR with the rejection code 2800 and <c>[sandbox:observar]</c> an acceptance with the
/// observation 4030. The ticket carries what the simulator needs to answer, so it keeps no state and the API and the workers can answer for each other.
/// </summary>
internal sealed partial class SandboxGreChannel(ICpePackager packager, TimeProvider clock) : IGreChannel
{
    public const string RejectMarker = "[sandbox:rechazar]";
    public const string RejectCdrMarker = "[sandbox:rechazar-cdr]";
    public const string ObserveMarker = "[sandbox:observar]";
    private const string TicketPrefix = "SBX.";
    private const string SunatRuc = "20131312955";

    [GeneratedRegex(@"^(?<ruc>\d{11})-(?<type>09|31)-(?<id>[A-Z0-9]{4}-\d{1,8})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BaseName();

    public Task<GreSubmitOutcome> SubmitAsync(GreSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var match = BaseName().Match(submission.FileBaseName ?? string.Empty);
        if (!match.Success)
        {
            return Task.FromResult(Refused("503", "El nombre del archivo no tiene el formato RUC-TIPO-SERIE-NUMERO."));
        }

        var content = packager.Unzip(submission.Zip);
        if (!content.IsSuccess)
        {
            return Task.FromResult(Refused("504", "El archivo ZIP no se pudo abrir."));
        }

        var xml = content.Value.Content;
        if (xml.Contains(RejectMarker, StringComparison.Ordinal))
        {
            return Task.FromResult(Refused("502", $"[Simulador] Rechazo simulado de {match.Groups["id"].Value} por la marca {RejectMarker}."));
        }

        var mode = xml.Contains(RejectCdrMarker, StringComparison.Ordinal) ? "R" : xml.Contains(ObserveMarker, StringComparison.Ordinal) ? "O" : "A";
        var reference = $"{match.Groups["ruc"].Value}/{match.Groups["id"].Value}/{mode}";
        var ticket = TicketPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(reference)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return Task.FromResult(new GreSubmitOutcome(GreSubmitStatus.Received, ticket, null, null));
    }

    public Task<GreTicketOutcome> QueryTicketAsync(GreChannelCredentials credentials, string ticket, CancellationToken cancellationToken)
    {
        if (ticket is null || !ticket.StartsWith(TicketPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(new GreTicketOutcome(GreTicketStatus.Error, null, "1033", "El ticket no fue emitido por el simulador."));
        }

        try
        {
            var encoded = ticket[TicketPrefix.Length..].Replace('-', '+').Replace('_', '/');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '='))).Split('/');
            var ruc = parts[0];
            var id = parts[1];
            return Task.FromResult(parts[2] switch
            {
                "R" => new GreTicketOutcome(GreTicketStatus.Error, CdrZip(ruc, id, $"La Guia de remision {id}, ha sido rechazada [Simulador: marca {RejectCdrMarker}]", 2800), "2800", "Rechazo simulado."),
                "O" => new GreTicketOutcome(GreTicketStatus.Done, CdrZip(ruc, id, $"La Guia de remision {id}, ha sido aceptada", 0, $"4030 - [Simulador] Observación simulada de {id} por la marca {ObserveMarker}."), null, null),
                _ => new GreTicketOutcome(GreTicketStatus.Done, CdrZip(ruc, id, $"La Guia de remision {id}, ha sido aceptada", 0), null, null),
            });
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            return Task.FromResult(new GreTicketOutcome(GreTicketStatus.Error, null, "1033", "El ticket del simulador no es válido."));
        }
    }

    private static GreSubmitOutcome Refused(string code, string message) => new(GreSubmitStatus.Refused, null, code, message);

    private byte[] CdrZip(string taxpayerRuc, string reference, string description, int code, params string[] notes)
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
