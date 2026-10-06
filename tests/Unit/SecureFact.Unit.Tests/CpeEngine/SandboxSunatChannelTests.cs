using Microsoft.Extensions.DependencyInjection;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.Unit.Tests.CpeEngine;

public class SandboxSunatChannelTests
{
    private static readonly SunatCredentials Credentials = new("20100066603", "USER", "pass");
    private readonly ZipCpePackager _packager = new();
    private readonly CdrParser _parser;

    public SandboxSunatChannelTests() => _parser = new CdrParser(_packager);

    private SandboxSunatChannel Channel() => new(_packager, TimeProvider.System);

    private byte[] Zip(string baseName, string content) => _packager.Zip(baseName, $"<Invoice><cbc:Description>{content}</cbc:Description></Invoice>").Value;

    [Fact]
    public async Task A_bill_is_accepted_with_a_cdr_that_names_the_document()
    {
        var reply = await Channel().SendBillAsync(Credentials, "20100066603-01-F001-12.zip", Zip("20100066603-01-F001-12", "Servicio"));

        Assert.Equal(ChannelOutcome.CdrReceived, reply.Outcome);
        var cdr = _parser.ParseZip(reply.CdrZip!).Value;
        Assert.Equal("F001-12", cdr.ReferenceId);
        Assert.Equal(0, cdr.ResponseCode);
        Assert.Equal("20100066603", cdr.TaxpayerRuc);
        Assert.Contains("La Factura numero F001-12", cdr.Description, StringComparison.Ordinal);
        Assert.Empty(cdr.Observations);
    }

    [Fact]
    public async Task The_observe_marker_gives_an_acceptance_with_the_observation_4030()
    {
        var reply = await Channel().SendBillAsync(Credentials, "20100066603-03-B001-3.zip", Zip("20100066603-03-B001-3", $"Item {SandboxSunatChannel.ObserveMarker}"));

        var cdr = _parser.ParseZip(reply.CdrZip!).Value;
        Assert.Equal(0, cdr.ResponseCode);
        var note = Assert.Single(cdr.Observations);
        Assert.Equal("4030", note.Code);
        Assert.Contains("Simulador", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reject_marker_gives_a_client_rejection_that_says_it_is_simulated()
    {
        var reply = await Channel().SendBillAsync(Credentials, "20100066603-01-F001-4.zip", Zip("20100066603-01-F001-4", SandboxSunatChannel.RejectMarker));

        Assert.Equal(ChannelOutcome.Fault, reply.Outcome);
        Assert.Equal(SunatSide.Client, reply.Fault!.Side);
        Assert.Equal(SunatCodeKind.Rejection, reply.Fault.Kind);
        Assert.False(reply.Fault.Retryable);
        Assert.Contains("Simulador", reply.Fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reject_cdr_marker_gives_a_cdr_with_the_rejection_code()
    {
        var reply = await Channel().SendBillAsync(Credentials, "20100066603-01-F001-5.zip", Zip("20100066603-01-F001-5", SandboxSunatChannel.RejectCdrMarker));

        Assert.Equal(ChannelOutcome.CdrReceived, reply.Outcome);
        var cdr = _parser.ParseZip(reply.CdrZip!).Value;
        Assert.Equal(2800, cdr.ResponseCode);
        Assert.Equal(CdrStatus.Rejected, cdr.Status);
        Assert.Contains("rechazada", cdr.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("factura.zip")]
    [InlineData("20100066603-09-F001-1.zip")]
    [InlineData("")]
    public async Task A_file_name_that_is_not_sunats_is_refused(string name)
    {
        var reply = await Channel().SendBillAsync(Credentials, name, Zip("20100066603-01-F001-1", "x"));

        Assert.Equal(151, reply.Fault!.Code);
    }

    [Fact]
    public async Task An_unreadable_zip_is_refused()
    {
        var reply = await Channel().SendBillAsync(Credentials, "20100066603-01-F001-1.zip", [1, 2, 3]);

        Assert.Equal(153, reply.Fault!.Code);
    }

    [Theory]
    [InlineData("RC-20261006-1", "El Resumen diario")]
    [InlineData("RA-20261006-2", "La Comunicacion de baja")]
    public async Task A_summary_ticket_carries_its_reference_and_the_status_answers_with_its_cdr(string id, string description)
    {
        var channel = Channel();

        var sent = await channel.SendSummaryAsync(Credentials, $"20100066603-{id}.zip", Zip($"20100066603-{id}", "x"));
        var status = await channel.GetStatusAsync(Credentials, sent.Ticket!);

        Assert.Equal(ChannelOutcome.TicketIssued, sent.Outcome);
        var cdr = _parser.ParseZip(status.CdrZip!).Value;
        Assert.Equal(id, cdr.ReferenceId);
        Assert.Equal("20100066603", cdr.TaxpayerRuc);
        Assert.Contains(description, cdr.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("SBX.@@@")]
    [InlineData("SBX.")]
    public async Task A_ticket_the_simulator_did_not_issue_is_refused(string ticket)
    {
        var reply = await Channel().GetStatusAsync(Credentials, ticket);

        Assert.Equal(1033, reply.Fault!.Code);
    }

    [Fact]
    public async Task A_summary_with_a_wrong_file_name_is_refused()
    {
        var reply = await Channel().SendSummaryAsync(Credentials, "resumen.zip", Zip("20100066603-01-F001-1", "x"));

        Assert.Equal(151, reply.Fault!.Code);
    }

    [Fact]
    public void The_simulator_is_registered_outside_production_and_refused_in_production()
    {
        var services = new ServiceCollection().AddSingleton<ICpePackager, ZipCpePackager>().AddSingleton(TimeProvider.System);

        services.AddSandboxSubmissionChannel(isProduction: false);

        Assert.IsType<SandboxSunatChannel>(services.BuildServiceProvider().GetRequiredService<ICpeSubmissionChannel>());
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSandboxSubmissionChannel(isProduction: true));
    }
}
