using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine;

internal sealed class EDocumentStateMachine : IEDocumentStateMachine
{
    public int MaxAttempts => 5;

    public Result<EDocumentSnapshot> Apply(EDocumentSnapshot current, EDocumentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.IsTerminal)
        {
            return Invalid(current, @event, "El documento ya tiene una respuesta final de SUNAT y no cambia.");
        }

        EDocumentSnapshot? next = (current.State, @event) switch
        {
            (EDocumentState.Pending, EDocumentEvent.DocumentSigned) => current with { State = EDocumentState.ReadyToSend },

            (EDocumentState.ReadyToSend, EDocumentEvent.SendStarted) when current.Attempts < MaxAttempts =>
                new(EDocumentState.Sending, current.Attempts + 1),

            (EDocumentState.Sending or EDocumentState.AwaitingTicket, EDocumentEvent.CdrAccepted) => current with { State = EDocumentState.Accepted },
            (EDocumentState.Sending or EDocumentState.AwaitingTicket, EDocumentEvent.CdrAcceptedWithObservations) => current with { State = EDocumentState.AcceptedWithObservations },
            (EDocumentState.Sending or EDocumentState.AwaitingTicket, EDocumentEvent.CdrRejected) => current with { State = EDocumentState.Rejected },

            (EDocumentState.Sending, EDocumentEvent.TicketIssued) => current with { State = EDocumentState.AwaitingTicket },
            (EDocumentState.Sending, EDocumentEvent.TransientFailure) => current with { State = current.Attempts < MaxAttempts ? EDocumentState.ReadyToSend : EDocumentState.Failed },

            // A status query may be repeated freely; it does not use up send attempts.
            (EDocumentState.AwaitingTicket, EDocumentEvent.StillProcessing or EDocumentEvent.TransientFailure) => current,

            (EDocumentState.Sending or EDocumentState.AwaitingTicket, EDocumentEvent.PermanentFailure) => current with { State = EDocumentState.Failed },

            (EDocumentState.Failed, EDocumentEvent.ManualRetry) => new(EDocumentState.ReadyToSend, 0),

            _ => null,
        };

        return next is null
            ? Invalid(current, @event, current.State == EDocumentState.ReadyToSend && @event == EDocumentEvent.SendStarted
                ? "Se agotaron los intentos de envío."
                : "Transición no permitida.")
            : next;
    }

    private static Error Invalid(EDocumentSnapshot current, EDocumentEvent @event, string reason) =>
        Error.Conflict(ErrorCodes.CpeInvalidTransition, "Transición de estado inválida", $"{reason} Estado: {current.State}, evento: {@event}.");
}
