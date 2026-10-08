using Microsoft.Extensions.DependencyInjection;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Gre;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Domain;
using SecureFact.Gre.Infrastructure;

namespace SecureFact.Unit.Tests.Gre;

public class GreDomainAndModuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.FromHours(-5));

    private static Guide NewGuide()
    {
        var series = GreSeries.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "T001", Now);
        return Guide.Prepare(Guid.NewGuid(), series.TenantId, series.CompanyId, series, 1, new DateOnly(2026, 10, 8), "01", "02", "6-20100070970", "CLIENTE", "{}", "20100066603-09-T001-1", "<x/>", "digest", Now);
    }

    [Fact]
    public void A_prepared_guide_that_is_sent_waits_for_its_ticket_and_then_takes_the_state_of_its_CDR()
    {
        var guide = NewGuide();
        Assert.Equal(GreState.Prepared, guide.State);
        Assert.False(guide.IsFinal);

        guide.MarkPending("ticket-1", Now);
        Assert.Equal(GreState.Pending, guide.State);
        Assert.Equal(Now + Guide.Backoff[0], guide.NextAttemptAt);

        guide.ApplyCdr([1], "p1", 0, "aceptada", "[]", hasObservations: true, Now);
        Assert.Equal(GreState.AcceptedWithObservations, guide.State);
        Assert.True(guide.IsFinal);
        Assert.Null(guide.NextAttemptAt);
    }

    [Fact]
    public void A_CDR_with_a_code_other_than_zero_rejects_the_guide()
    {
        var guide = NewGuide();
        guide.MarkPending("ticket-1", Now);

        guide.ApplyCdr([1], "p1", 2800, "rechazada", "[]", hasObservations: false, Now);

        Assert.Equal(GreState.Rejected, guide.State);
    }

    [Fact]
    public void The_retries_of_a_send_back_off_and_the_guide_fails_after_the_last_attempt()
    {
        var guide = NewGuide();

        for (var attempt = 1; attempt < Guide.MaxAttempts; attempt++)
        {
            guide.RecordTransient("SF-GRE-NETWORK", "sin red", Now);
            Assert.Equal(GreState.Prepared, guide.State);
            Assert.Equal(Now + Guide.Backoff[Math.Min(attempt, Guide.Backoff.Length) - 1], guide.NextAttemptAt);
        }

        guide.RecordTransient("SF-GRE-NETWORK", new string('x', 900), Now);

        Assert.Equal(GreState.Failed, guide.State);
        Assert.Equal(500, guide.ErrorMessage!.Length);
        Assert.Null(guide.NextAttemptAt);
    }

    [Fact]
    public void A_ticket_that_never_answers_ends_in_a_timeout()
    {
        var guide = NewGuide();
        guide.MarkPending("ticket-1", Now);

        for (var attempt = 1; attempt < Guide.MaxAttempts - 1; attempt++)
        {
            guide.ScheduleNextCheck(Now);
            Assert.Equal(GreState.Pending, guide.State);
        }

        guide.ScheduleNextCheck(Now);

        Assert.Equal(GreState.Failed, guide.State);
        Assert.Equal("SF-GRE-TIMEOUT", guide.ErrorCode);
    }

    [Fact]
    public void A_series_can_be_deactivated()
    {
        var series = GreSeries.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "T002", Now);
        Assert.True(series.IsActive);

        series.Deactivate(Now.AddMinutes(1));

        Assert.False(series.IsActive);
        Assert.Equal(Now.AddMinutes(1), series.UpdatedAt);
    }

    [Fact]
    public async Task Without_a_named_environment_the_guides_wait_in_the_unconfigured_channel()
    {
        var channel = new UnconfiguredGreChannel();
        var credentials = new GreChannelCredentials("20100066603", "MODDATOS", "x", "id", "secret");

        var sent = await channel.SubmitAsync(new GreSubmission(credentials, "09", "T001", 1, "20100066603-09-T001-1", [1]), CancellationToken.None);
        var asked = await channel.QueryTicketAsync(credentials, "t", CancellationToken.None);

        Assert.Equal(GreSubmitStatus.Transient, sent.Status);
        Assert.Equal(GreTicketStatus.Transient, asked.Status);
    }

    [Fact]
    public void The_simulator_is_refused_in_production_and_registered_elsewhere()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddSandboxGreChannel(isProduction: true));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICpePackager>(new ZipCpePackager());
        services.AddSandboxGreChannel(isProduction: false);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SandboxGreChannel>(provider.GetRequiredService<IGreChannel>());
    }

    [Fact]
    public void The_REST_channel_is_registered_with_the_addresses_that_were_named()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);

        services.AddGreSubmissionChannel(GreChannelOptions.Production);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<GreRestChannel>(provider.GetRequiredService<IGreChannel>());
        Assert.Throws<ArgumentNullException>(() => services.AddGreSubmissionChannel(null!));
    }
}
