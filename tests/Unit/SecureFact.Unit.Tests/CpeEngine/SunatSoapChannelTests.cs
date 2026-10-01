using System.Net;
using System.Text;
using System.Xml.Linq;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.Unit.Tests.CpeEngine;

public class SunatSoapChannelTests
{
    private static readonly SunatCredentials Credentials = new("20100066603", "MODDATOS", "s3cr3t-p@ss&<x>");
    private static readonly byte[] Zip = [0x50, 0x4B, 3, 4, 9, 9, 9];

    private sealed class StubHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, LastBody ?? string.Empty, cancellationToken);
        }
    }

    private static (SunatSoapChannel Channel, StubHandler Handler) Channel(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new StubHandler(respond);
        return (new SunatSoapChannel(new HttpClient(handler), new SunatChannelOptions(new Uri("https://sunat.test/billService"), timeout ?? TimeSpan.FromSeconds(5))), handler);
    }

    private static Task<HttpResponseMessage> Reply(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") });

    private static string Envelope(string body) =>
        $"<S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\"><S:Body>{body}</S:Body></S:Envelope>";

    [Fact]
    public async Task SendBill_builds_the_envelope_of_the_manual_and_returns_the_cdr_zip()
    {
        var cdr = new byte[] { 1, 2, 3, 4, 5 };
        var (channel, handler) = Channel((_, _, _) => Reply(Envelope(
            $"<ns2:sendBillResponse xmlns:ns2=\"http://service.sunat.gob.pe\"><applicationResponse>{Convert.ToBase64String(cdr)}</applicationResponse></ns2:sendBillResponse>")));

        var reply = await channel.SendBillAsync(Credentials, "20100066603-01-F001-1.zip", Zip);

        Assert.Equal(ChannelOutcome.CdrReceived, reply.Outcome);
        Assert.Equal(cdr, reply.CdrZip);

        var sent = XDocument.Parse(handler.LastBody!);
        XNamespace wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace ser = "http://service.sunat.gob.pe";
        Assert.Equal("20100066603MODDATOS", sent.Descendants(wsse + "Username").Single().Value);
        Assert.Equal("s3cr3t-p@ss&<x>", sent.Descendants(wsse + "Password").Single().Value); // escaped by the XML writer, round-trips intact
        var call = sent.Descendants(ser + "sendBill").Single();
        Assert.Equal("20100066603-01-F001-1.zip", call.Element("fileName")!.Value);
        Assert.Equal(Zip, Convert.FromBase64String(call.Element("contentFile")!.Value));
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task SendSummary_returns_the_ticket()
    {
        var (channel, _) = Channel((_, body, _) =>
        {
            Assert.Contains("sendSummary", body, StringComparison.Ordinal);
            return Reply(Envelope("<ns2:sendSummaryResponse xmlns:ns2=\"http://service.sunat.gob.pe\"><ticket>201100000011227</ticket></ns2:sendSummaryResponse>"));
        });

        var reply = await channel.SendSummaryAsync(Credentials, "20100066603-RC-20260930-1.zip", Zip);

        Assert.Equal(ChannelOutcome.TicketIssued, reply.Outcome);
        Assert.Equal("201100000011227", reply.Ticket);
    }

    [Theory]
    [InlineData("0", ChannelOutcome.CdrReceived)]
    [InlineData("99", ChannelOutcome.CdrReceived)]
    [InlineData("98", ChannelOutcome.InProgress)]
    [InlineData("42", ChannelOutcome.Unreachable)]
    public async Task GetStatus_maps_the_status_codes_of_the_manual(string code, ChannelOutcome expected)
    {
        var content = Convert.ToBase64String([7, 7, 7]);
        var (channel, handler) = Channel((_, _, _) => Reply(Envelope(
            $"<ns2:getStatusResponse xmlns:ns2=\"http://service.sunat.gob.pe\"><status><content>{(code is "0" or "99" ? content : string.Empty)}</content><statusCode>{code}</statusCode></status></ns2:getStatusResponse>")));

        var reply = await channel.GetStatusAsync(Credentials, "201100000011227");

        Assert.Equal(expected, reply.Outcome);
        Assert.Contains("<ticket>201100000011227</ticket>", handler.LastBody, StringComparison.Ordinal);
        if (expected == ChannelOutcome.CdrReceived)
        {
            Assert.Equal(new byte[] { 7, 7, 7 }, reply.CdrZip);
        }
    }

    [Theory]
    [InlineData("soap-env:Server.0835", SunatSide.Server, 835, true)]
    [InlineData("soap-env:Client.0111", SunatSide.Client, 111, true)]  // 0100-0999 is a SUNAT exception: retryable
    [InlineData("soap-env:Client.1033", SunatSide.Client, 1033, false)]
    [InlineData("soap-env:Server", SunatSide.Server, null, true)]
    [InlineData("soap-env:Client", SunatSide.Client, null, false)]
    [InlineData("weird", SunatSide.Unknown, null, false)]
    public async Task Soap_faults_are_parsed_and_classified(string faultCode, SunatSide side, int? code, bool retryable)
    {
        var (channel, _) = Channel((_, _, _) => Reply(
            Envelope($"<soap-env:Fault xmlns:soap-env=\"http://schemas.xmlsoap.org/soap/envelope/\"><faultcode>{faultCode}</faultcode><faultstring>descripción del error</faultstring></soap-env:Fault>"),
            HttpStatusCode.InternalServerError));

        var reply = await channel.SendBillAsync(Credentials, "20100066603-01-F001-1.zip", Zip);

        Assert.Equal(ChannelOutcome.Fault, reply.Outcome);
        Assert.Equal(side, reply.Fault!.Side);
        Assert.Equal(code, reply.Fault.Code);
        Assert.Equal(retryable, reply.Fault.Retryable);
        Assert.Equal("descripción del error", reply.Fault.Message);
    }

    [Fact]
    public async Task A_long_fault_message_is_truncated()
    {
        var (channel, _) = Channel((_, _, _) => Reply(Envelope(
            $"<Fault><faultcode>Client.0100</faultcode><faultstring>{new string('x', 5000)}</faultstring></Fault>"), HttpStatusCode.InternalServerError));

        var reply = await channel.GetStatusAsync(Credentials, "1");

        Assert.Equal(500, reply.Fault!.Message.Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ChannelOutcome.Unreachable, true)]
    [InlineData(HttpStatusCode.BadGateway, ChannelOutcome.Unreachable, true)]
    [InlineData(HttpStatusCode.Unauthorized, ChannelOutcome.Fault, false)]
    [InlineData(HttpStatusCode.Forbidden, ChannelOutcome.Fault, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ChannelOutcome.Fault, true)]
    public async Task Http_errors_without_a_soap_body_are_classified(HttpStatusCode status, ChannelOutcome outcome, bool retryable)
    {
        var (channel, _) = Channel((_, _, _) => Reply("<html>gateway</html>", status));

        var reply = await channel.SendBillAsync(Credentials, "20100066603-01-F001-1.zip", Zip);

        Assert.Equal(outcome, reply.Outcome);
        Assert.Equal(retryable, reply.Fault!.Retryable);
    }

    [Fact]
    public async Task Network_failures_and_timeouts_are_reported_not_thrown()
    {
        var (down, _) = Channel((_, _, _) => throw new HttpRequestException("boom: s3cr3t-p@ss"));
        var (slow, _) = Channel(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage();
        }, TimeSpan.FromMilliseconds(50));

        var first = await down.SendBillAsync(Credentials, "a-b-c-d-e.zip", Zip);
        var second = await slow.SendBillAsync(Credentials, "a-b-c-d-e.zip", Zip);

        Assert.Equal(ChannelOutcome.Unreachable, first.Outcome);
        Assert.Equal(ChannelOutcome.Unreachable, second.Outcome);
        Assert.True(first.Fault!.Retryable && second.Fault!.Retryable);
        Assert.DoesNotContain("s3cr3t", first.Fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_swallowed()
    {
        var (channel, _) = Channel(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage();
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendBillAsync(Credentials, "a-b-c-d-e.zip", Zip, cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Envelope><Body/></Envelope>")]
    [InlineData("<S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\"><S:Body><x/></S:Body></S:Envelope>")]
    public async Task Garbled_success_responses_are_unreachable_not_accepted(string xml)
    {
        var (channel, _) = Channel((_, _, _) => Reply(xml));

        var reply = await channel.SendBillAsync(Credentials, "20100066603-01-F001-1.zip", Zip);

        Assert.Equal(ChannelOutcome.Unreachable, reply.Outcome);
        Assert.Null(reply.CdrZip);
    }

    [Fact]
    public async Task An_invalid_base64_cdr_is_not_accepted()
    {
        var (channel, _) = Channel((_, _, _) => Reply(Envelope("<sendBillResponse><applicationResponse>***not-base64***</applicationResponse></sendBillResponse>")));

        var reply = await channel.SendBillAsync(Credentials, "20100066603-01-F001-1.zip", Zip);

        Assert.Equal(ChannelOutcome.Unreachable, reply.Outcome);
    }

    [Fact]
    public async Task Hostile_dtds_in_responses_are_never_processed()
    {
        var (channel, _) = Channel((_, _, _) => Reply("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\"><S:Body>&x;</S:Body></S:Envelope>"));

        var reply = await channel.SendBillAsync(Credentials, "20100066603-01-F001-1.zip", Zip);

        Assert.Equal(ChannelOutcome.Unreachable, reply.Outcome);
    }

    [Fact]
    public async Task Missing_credentials_or_parameters_fail_before_any_request()
    {
        var calls = 0;
        var (channel, _) = Channel((_, _, _) =>
        {
            calls++;
            return Reply(string.Empty);
        });

        var noPassword = await channel.SendBillAsync(Credentials with { SolPassword = string.Empty }, "a-b-c-d-e.zip", Zip);
        var noFile = await channel.SendBillAsync(Credentials, string.Empty, Zip);
        var noTicket = await channel.GetStatusAsync(Credentials, " ");

        Assert.All([noPassword, noFile, noTicket], r =>
        {
            Assert.Equal(ChannelOutcome.Fault, r.Outcome);
            Assert.False(r.Fault!.Retryable);
        });
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Credentials_never_print_the_password_and_the_endpoint_must_be_https()
    {
        Assert.DoesNotContain("s3cr3t", Credentials.ToString(), StringComparison.Ordinal);
        Assert.Equal("20100066603MODDATOS", Credentials.UserName);

        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new SunatSoapChannel(http, new SunatChannelOptions(new Uri("http://e-factura.sunat.gob.pe/x"), TimeSpan.FromSeconds(1))));
        _ = new SunatSoapChannel(http, new SunatChannelOptions(new Uri("http://localhost:5000/simulator"), TimeSpan.FromSeconds(1)));
        Assert.Equal("e-factura.sunat.gob.pe", SunatChannelOptions.Production.Endpoint.Host);
        Assert.Equal("e-beta.sunat.gob.pe", SunatChannelOptions.Beta.Endpoint.Host);
    }
}
