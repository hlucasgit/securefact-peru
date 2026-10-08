using SecureFact.Gre.Contracts;

namespace SecureFact.Gre.Infrastructure;

/// <summary>
/// The channel of a host that did not name a SUNAT environment for the guides. SUNAT is never reached implicitly. Everything is transient, so nothing is lost: the guide stays prepared and
/// is sent once a channel is configured.
/// </summary>
internal sealed class UnconfiguredGreChannel : IGreChannel
{
    private const string Code = "SF-GRE-NOCHANNEL";
    private const string Message = "El envío de guías a SUNAT no está configurado en este entorno.";

    public Task<GreSubmitOutcome> SubmitAsync(GreSubmission submission, CancellationToken cancellationToken) =>
        Task.FromResult(new GreSubmitOutcome(GreSubmitStatus.Transient, null, Code, Message));

    public Task<GreTicketOutcome> QueryTicketAsync(GreChannelCredentials credentials, string ticket, CancellationToken cancellationToken) =>
        Task.FromResult(new GreTicketOutcome(GreTicketStatus.Transient, null, Code, Message));
}
