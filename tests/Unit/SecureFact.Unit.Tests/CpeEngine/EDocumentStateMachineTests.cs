using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;

namespace SecureFact.Unit.Tests.CpeEngine;

public class EDocumentStateMachineTests
{
    private readonly EDocumentStateMachine _machine = new();

    private EDocumentSnapshot Run(EDocumentSnapshot start, params EDocumentEvent[] events)
    {
        var current = start;
        foreach (var e in events)
        {
            var result = _machine.Apply(current, e);
            Assert.True(result.IsSuccess, $"{current.State} + {e}: {(result.IsSuccess ? string.Empty : result.Error.Detail)}");
            current = result.Value;
        }

        return current;
    }

    [Fact]
    public void The_happy_path_ends_accepted()
    {
        var end = Run(EDocumentSnapshot.New, EDocumentEvent.DocumentSigned, EDocumentEvent.SendStarted, EDocumentEvent.CdrAccepted);

        Assert.Equal(EDocumentState.Accepted, end.State);
        Assert.True(end.IsTerminal);
        Assert.Equal(1, end.Attempts);
    }

    [Theory]
    [InlineData(EDocumentEvent.CdrAcceptedWithObservations, EDocumentState.AcceptedWithObservations)]
    [InlineData(EDocumentEvent.CdrRejected, EDocumentState.Rejected)]
    public void A_cdr_decides_the_final_state(EDocumentEvent cdr, EDocumentState expected) =>
        Assert.Equal(expected, Run(EDocumentSnapshot.New, EDocumentEvent.DocumentSigned, EDocumentEvent.SendStarted, cdr).State);

    [Fact]
    public void The_asynchronous_path_waits_for_the_ticket_without_using_attempts()
    {
        var waiting = Run(EDocumentSnapshot.New, EDocumentEvent.DocumentSigned, EDocumentEvent.SendStarted, EDocumentEvent.TicketIssued);

        var after = Run(waiting, EDocumentEvent.StillProcessing, EDocumentEvent.TransientFailure, EDocumentEvent.StillProcessing, EDocumentEvent.CdrAccepted);

        Assert.Equal(EDocumentState.AwaitingTicket, waiting.State);
        Assert.Equal(1, after.Attempts);
        Assert.Equal(EDocumentState.Accepted, after.State);
    }

    [Fact]
    public void A_transient_failure_returns_to_signed_until_attempts_run_out()
    {
        var current = Run(EDocumentSnapshot.New, EDocumentEvent.DocumentSigned);
        for (var i = 1; i < _machine.MaxAttempts; i++)
        {
            current = Run(current, EDocumentEvent.SendStarted, EDocumentEvent.TransientFailure);
            Assert.Equal(EDocumentState.ReadyToSend, current.State);
            Assert.Equal(i, current.Attempts);
        }

        current = Run(current, EDocumentEvent.SendStarted, EDocumentEvent.TransientFailure);

        Assert.Equal(EDocumentState.Failed, current.State);
        Assert.Equal(_machine.MaxAttempts, current.Attempts);
    }

    [Fact]
    public void Sending_is_refused_when_attempts_are_exhausted()
    {
        var exhausted = new EDocumentSnapshot(EDocumentState.ReadyToSend, _machine.MaxAttempts);

        var result = _machine.Apply(exhausted, EDocumentEvent.SendStarted);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.CpeInvalidTransition, result.Error.Code);
    }

    [Fact]
    public void Failed_documents_leave_only_by_manual_retry_which_resets_attempts()
    {
        var failed = Run(EDocumentSnapshot.New, EDocumentEvent.DocumentSigned, EDocumentEvent.SendStarted, EDocumentEvent.PermanentFailure);

        Assert.Equal(EDocumentState.Failed, failed.State);
        foreach (var e in Enum.GetValues<EDocumentEvent>().Where(e => e != EDocumentEvent.ManualRetry))
        {
            Assert.False(_machine.Apply(failed, e).IsSuccess, e.ToString());
        }

        var retried = Run(failed, EDocumentEvent.ManualRetry);

        Assert.Equal(new EDocumentSnapshot(EDocumentState.ReadyToSend, 0), retried);
    }

    [Theory]
    [InlineData(EDocumentState.Accepted)]
    [InlineData(EDocumentState.AcceptedWithObservations)]
    [InlineData(EDocumentState.Rejected)]
    public void A_terminal_document_never_changes(EDocumentState terminal)
    {
        foreach (var e in Enum.GetValues<EDocumentEvent>())
        {
            var result = _machine.Apply(new EDocumentSnapshot(terminal, 2), e);

            Assert.False(result.IsSuccess, $"{terminal} + {e}");
            Assert.Equal(ErrorCodes.CpeInvalidTransition, result.Error.Code);
        }
    }

    [Fact]
    public void Every_state_event_pair_not_in_the_documented_table_is_rejected()
    {
        var allowed = new HashSet<(EDocumentState, EDocumentEvent)>
        {
            (EDocumentState.Pending, EDocumentEvent.DocumentSigned),
            (EDocumentState.ReadyToSend, EDocumentEvent.SendStarted),
            (EDocumentState.Sending, EDocumentEvent.CdrAccepted),
            (EDocumentState.Sending, EDocumentEvent.CdrAcceptedWithObservations),
            (EDocumentState.Sending, EDocumentEvent.CdrRejected),
            (EDocumentState.Sending, EDocumentEvent.TicketIssued),
            (EDocumentState.Sending, EDocumentEvent.TransientFailure),
            (EDocumentState.Sending, EDocumentEvent.PermanentFailure),
            (EDocumentState.AwaitingTicket, EDocumentEvent.CdrAccepted),
            (EDocumentState.AwaitingTicket, EDocumentEvent.CdrAcceptedWithObservations),
            (EDocumentState.AwaitingTicket, EDocumentEvent.CdrRejected),
            (EDocumentState.AwaitingTicket, EDocumentEvent.StillProcessing),
            (EDocumentState.AwaitingTicket, EDocumentEvent.TransientFailure),
            (EDocumentState.AwaitingTicket, EDocumentEvent.PermanentFailure),
            (EDocumentState.Sending, EDocumentEvent.ReturnedToQueue),
            (EDocumentState.AwaitingTicket, EDocumentEvent.ReturnedToQueue),
            (EDocumentState.Failed, EDocumentEvent.ManualRetry),
        };

        foreach (var state in Enum.GetValues<EDocumentState>())
        {
            foreach (var e in Enum.GetValues<EDocumentEvent>())
            {
                var result = _machine.Apply(new EDocumentSnapshot(state, 0), e);
                Assert.Equal(allowed.Contains((state, e)), result.IsSuccess);
            }
        }
    }
}
