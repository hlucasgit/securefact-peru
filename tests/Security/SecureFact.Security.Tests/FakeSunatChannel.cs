using System.Collections.Concurrent;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>
/// In-process stand-in for SUNAT's billService. It records what the pipeline sends (so tests can inspect credentials and packages)
/// and answers with scripted replies. Nothing here touches the network or SUNAT's beta service.
/// </summary>
public sealed class FakeSunatChannel : ICpeSubmissionChannel
{
    public sealed record Call(SunatCredentials Credentials, string ZipFileName, byte[] Zip);

    private readonly ConcurrentQueue<Func<Call, ChannelReply>> _script = new();
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly ConcurrentQueue<Func<Call, ChannelReply>> _summaryScript = new();
    private readonly ConcurrentQueue<Call> _summaryCalls = new();
    private readonly ConcurrentQueue<ChannelReply> _statusScript = new();
    private readonly ConcurrentQueue<string> _statusCalls = new();
    private volatile Func<Call, ChannelReply>? _billHandler;

    public IReadOnlyCollection<Call> Calls => [.. _calls];

    public IReadOnlyCollection<Call> SummaryCalls => [.. _summaryCalls];

    public IReadOnlyCollection<string> StatusCalls => [.. _statusCalls];

    public void Reset()
    {
        _script.Clear();
        _calls.Clear();
        _summaryScript.Clear();
        _summaryCalls.Clear();
        _statusScript.Clear();
        _statusCalls.Clear();
        _billHandler = null;
    }

    public void Enqueue(ChannelReply reply) => _script.Enqueue(_ => reply);

    public void Enqueue(Func<Call, ChannelReply> reply) => _script.Enqueue(reply);

    /// <summary>Answers every sendBill call with this function instead of the queue (for tests that share the database with other documents).</summary>
    public void RespondToBills(Func<Call, ChannelReply> handler) => _billHandler = handler;

    public void EnqueueSummary(ChannelReply reply) => _summaryScript.Enqueue(_ => reply);

    public void EnqueueStatus(ChannelReply reply) => _statusScript.Enqueue(reply);

    public Task<ChannelReply> SendBillAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default)
    {
        var call = new Call(credentials, zipFileName, zip);
        _calls.Enqueue(call);
        if (_billHandler is { } handler)
        {
            return Task.FromResult(handler(call));
        }

        return Task.FromResult(_script.TryDequeue(out var reply) ? reply(call) : ChannelReply.Down("simulator has no scripted reply"));
    }

    public Task<ChannelReply> SendSummaryAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default)
    {
        var call = new Call(credentials, zipFileName, zip);
        _summaryCalls.Enqueue(call);
        return Task.FromResult(_summaryScript.TryDequeue(out var reply) ? reply(call) : ChannelReply.Down("simulator has no scripted summary reply"));
    }

    public Task<ChannelReply> GetStatusAsync(SunatCredentials credentials, string ticket, CancellationToken cancellationToken = default)
    {
        _statusCalls.Enqueue(ticket);
        return Task.FromResult(_statusScript.TryDequeue(out var reply) ? reply : ChannelReply.Down("simulator has no scripted status reply"));
    }

    /// <summary>A CDR ZIP with the structure of the Programmer Manual's examples (Annex 1).</summary>
    public static byte[] CdrZip(string taxpayerRuc, string reference, string code = "0", string description = "ha sido aceptada", params string[] notes)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"ISO-8859-1\" standalone=\"no\"?>" +
            "<ar:ApplicationResponse xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" " +
            "xmlns:ar=\"urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2\" " +
            "xmlns:cac=\"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2\" " +
            "xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\" " +
            "xmlns:ext=\"urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2\">" +
            "<ext:UBLExtensions><ext:UBLExtension><ext:ExtensionContent/></ext:UBLExtension></ext:UBLExtensions>" +
            "<cbc:UBLVersionID>2.0</cbc:UBLVersionID><cbc:CustomizationID>1.0</cbc:CustomizationID>" +
            "<cbc:ID>201200000230061</cbc:ID><cbc:IssueDate>2026-10-01</cbc:IssueDate><cbc:IssueTime>10:09:27</cbc:IssueTime>" +
            "<cbc:ResponseDate>2026-10-01</cbc:ResponseDate><cbc:ResponseTime>10:09:30</cbc:ResponseTime>" +
            string.Concat(notes.Select(n => $"<cbc:Note>{n}</cbc:Note>")) +
            "<cac:SenderParty><cac:PartyIdentification><cbc:ID>20131312955</cbc:ID></cac:PartyIdentification></cac:SenderParty>" +
            $"<cac:ReceiverParty><cac:PartyIdentification><cbc:ID>{taxpayerRuc}</cbc:ID></cac:PartyIdentification></cac:ReceiverParty>" +
            "<cac:DocumentResponse><cac:Response>" +
            $"<cbc:ReferenceID>{reference}</cbc:ReferenceID><cbc:ResponseCode>{code}</cbc:ResponseCode><cbc:Description>{description}</cbc:Description>" +
            "</cac:Response></cac:DocumentResponse></ar:ApplicationResponse>";
        return new ZipCpePackager().Zip("R-" + reference, xml).Value;
    }
}
