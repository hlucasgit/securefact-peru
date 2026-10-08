using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Infrastructure;

namespace SecureFact.Unit.Tests.Gre;

public class GreChannelTests
{
    private static readonly byte[] Zip = [0x50, 0x4B, 3, 4, 9, 9, 9];

    private sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private sealed class StubHandler(Func<Seen, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(seen);
            return respond(seen);
        }
    }

    private static GreChannelCredentials Credentials(string clientId) => new("20100066603", "MODDATOS", "sol-clave-9", clientId, "client-secret-7");

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Token(string value = "tok-1") => Json(HttpStatusCode.OK, $$"""{"access_token":"{{value}}","token_type":"Bearer","expires_in":3600}""");

    private static (GreRestChannel Channel, StubHandler Handler) Channel(Func<Seen, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var options = new GreChannelOptions(new Uri("https://seguridad.test"), new Uri("https://api.test"), TimeSpan.FromSeconds(5));
        return (new GreRestChannel(new HttpClient(handler), options, TimeProvider.System), handler);
    }

    private static GreSubmission Submission(GreChannelCredentials credentials) => new(credentials, "09", "T001", 7, "20100066603-09-T001-7", Zip);

    private static HttpResponseMessage ApiOrToken(Seen seen, Func<Seen, HttpResponseMessage> api) => seen.Uri.Host == "seguridad.test" ? Token() : api(seen);

    [Fact]
    public async Task The_token_is_asked_with_the_password_grant_and_the_file_goes_with_its_hash()
    {
        var (channel, handler) = Channel(seen => ApiOrToken(seen, _ => Json(HttpStatusCode.OK, """{"numTicket":"abc-123","fecRecepcion":"2026-10-08T10:00:00"}""")));

        var outcome = await channel.SubmitAsync(Submission(Credentials("client-grant")), CancellationToken.None);

        Assert.Equal(GreSubmitStatus.Received, outcome.Status);
        Assert.Equal("abc-123", outcome.Ticket);
        var token = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://seguridad.test/v1/clientessol/client-grant/oauth2/token/", token.Uri.ToString());
        Assert.Contains("grant_type=password", token.Body, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Fapi-cpe.sunat.gob.pe", token.Body, StringComparison.Ordinal);
        Assert.Contains("username=20100066603MODDATOS", token.Body, StringComparison.Ordinal);

        var send = handler.Requests[1];
        Assert.Equal("https://api.test/v1/contribuyente/gem/comprobantes/20100066603-09-T001-7", send.Uri.ToString());
        Assert.Equal("Bearer tok-1", send.Authorization);
        using var body = JsonDocument.Parse(send.Body);
        var file = body.RootElement.GetProperty("archivo");
        Assert.Equal("20100066603-09-T001-7.zip", file.GetProperty("nomArchivo").GetString());
        Assert.Equal(Convert.ToBase64String(Zip), file.GetProperty("arcGreZip").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Zip)), file.GetProperty("hashZip").GetString());
    }

    [Fact]
    public async Task The_token_is_reused_while_it_lasts()
    {
        var (channel, handler) = Channel(seen => ApiOrToken(seen, _ => Json(HttpStatusCode.OK, """{"codRespuesta":"98"}""")));
        var credentials = Credentials("client-reuse");

        await channel.QueryTicketAsync(credentials, "t1", CancellationToken.None);
        await channel.QueryTicketAsync(credentials, "t2", CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Uri.Host == "seguridad.test");
    }

    [Fact]
    public async Task A_revoked_token_is_asked_again_once()
    {
        var calls = 0;
        var (channel, handler) = Channel(seen => ApiOrToken(seen, _ => ++calls == 1 ? Json(HttpStatusCode.Unauthorized, "{}") : Json(HttpStatusCode.OK, """{"codRespuesta":"98"}""")));

        var outcome = await channel.QueryTicketAsync(Credentials("client-revoked"), "t1", CancellationToken.None);

        Assert.Equal(GreTicketStatus.InProcess, outcome.Status);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.Host == "seguridad.test"));
    }

    [Fact]
    public async Task A_ticket_in_process_and_a_ticket_done_are_told_apart()
    {
        var cdr = new byte[] { 1, 2, 3 };
        var answers = new Queue<string>(["""{"codRespuesta":"98"}""", $$"""{"codRespuesta":"0","arcCdr":"{{Convert.ToBase64String(cdr)}}","indCdrGenerado":"1"}"""]);
        var (channel, _) = Channel(seen => ApiOrToken(seen, _ => Json(HttpStatusCode.OK, answers.Dequeue())));
        var credentials = Credentials("client-done");

        var waiting = await channel.QueryTicketAsync(credentials, "t1", CancellationToken.None);
        var done = await channel.QueryTicketAsync(credentials, "t1", CancellationToken.None);

        Assert.Equal(GreTicketStatus.InProcess, waiting.Status);
        Assert.Equal(GreTicketStatus.Done, done.Status);
        Assert.Equal(cdr, done.CdrZip);
    }

    [Fact]
    public async Task A_ticket_with_error_carries_the_error_of_SUNAT_and_its_CDR()
    {
        var cdr = new byte[] { 9, 9 };
        var (channel, _) = Channel(seen => ApiOrToken(seen, _ => Json(HttpStatusCode.OK,
            $$"""{"codRespuesta":"99","error":{"numError":"2800","desError":"Rechazada"},"arcCdr":"{{Convert.ToBase64String(cdr)}}","indCdrGenerado":"1"}""")));

        var outcome = await channel.QueryTicketAsync(Credentials("client-error"), "t1", CancellationToken.None);

        Assert.Equal(GreTicketStatus.Error, outcome.Status);
        Assert.Equal("2800", outcome.ErrorCode);
        Assert.Equal("Rechazada", outcome.ErrorMessage);
        Assert.Equal(cdr, outcome.CdrZip);
    }

    [Fact]
    public async Task A_refusal_of_the_shape_is_final_and_a_server_failure_is_transient()
    {
        var status = HttpStatusCode.UnprocessableEntity;
        var (channel, _) = Channel(seen => ApiOrToken(seen, _ => Json(status, """{"cod":"503","msg":"Nombre de archivo inválido","errors":[{"cod":"1","msg":"detalle"}]}""")));
        var credentials = Credentials("client-refusal");

        var refused = await channel.SubmitAsync(Submission(credentials), CancellationToken.None);
        status = HttpStatusCode.BadGateway;
        var transient = await channel.SubmitAsync(Submission(credentials), CancellationToken.None);
        status = HttpStatusCode.TooManyRequests;
        var limited = await channel.SubmitAsync(Submission(credentials), CancellationToken.None);

        Assert.Equal(GreSubmitStatus.Refused, refused.Status);
        Assert.Equal("503", refused.ErrorCode);
        Assert.Contains("Nombre de archivo inválido", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("detalle", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(GreSubmitStatus.Transient, transient.Status);
        Assert.Equal(GreSubmitStatus.Transient, limited.Status);
    }

    [Fact]
    public async Task A_rejected_authentication_is_transient_and_never_repeats_what_was_sent()
    {
        var (channel, _) = Channel(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"client-secret-7 sol-clave-9"}"""));

        var outcome = await channel.SubmitAsync(Submission(Credentials("client-badauth")), CancellationToken.None);

        Assert.Equal(GreSubmitStatus.Transient, outcome.Status);
        Assert.Equal("SF-GRE-AUTH", outcome.ErrorCode);
        Assert.DoesNotContain("client-secret-7", outcome.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("sol-clave-9", outcome.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_failure_is_transient()
    {
        var (channel, _) = Channel(_ => throw new HttpRequestException("down"));

        var outcome = await channel.SubmitAsync(Submission(Credentials("client-network")), CancellationToken.None);

        Assert.Equal(GreSubmitStatus.Transient, outcome.Status);
    }

    [Fact]
    public void Plain_HTTP_is_refused_except_in_loopback()
    {
        var plain = new GreChannelOptions(new Uri("http://seguridad.test"), new Uri("https://api.test"), TimeSpan.FromSeconds(5));
        var loopback = new GreChannelOptions(new Uri("http://localhost:5000"), new Uri("http://127.0.0.1:5001"), TimeSpan.FromSeconds(5));

        Assert.Throws<ArgumentException>(() => new GreRestChannel(new HttpClient(), plain, TimeProvider.System));
        Assert.NotNull(new GreRestChannel(new HttpClient(), loopback, TimeProvider.System));
    }

    [Fact]
    public void The_credentials_never_show_their_secrets()
    {
        var text = Credentials("client-text").ToString();

        Assert.DoesNotContain("client-secret-7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sol-clave-9", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_simulator_accepts_observes_and_rejects_by_the_markers_of_the_file()
    {
        var packager = new ZipCpePackager();
        var simulator = new SandboxGreChannel(packager, TimeProvider.System);
        var credentials = Credentials("client-sbx");

        async Task<GreTicketOutcome> RunAsync(string marker)
        {
            var zip = packager.Zip("20100066603-09-T001-7", $"<DespatchAdvice><cbc:Note>{marker}</cbc:Note></DespatchAdvice>").Value;
            var sent = await simulator.SubmitAsync(Submission(credentials) with { Zip = zip }, CancellationToken.None);
            Assert.Equal(GreSubmitStatus.Received, sent.Status);
            return await simulator.QueryTicketAsync(credentials, sent.Ticket!, CancellationToken.None);
        }

        var accepted = await RunAsync("ok");
        var observed = await RunAsync(SandboxGreChannel.ObserveMarker);
        var rejected = await RunAsync(SandboxGreChannel.RejectCdrMarker);

        Assert.Equal(GreTicketStatus.Done, accepted.Status);
        Assert.Equal(GreTicketStatus.Done, observed.Status);
        Assert.Equal(GreTicketStatus.Error, rejected.Status);
        Assert.NotNull(rejected.CdrZip);

        var refusedZip = packager.Zip("20100066603-09-T001-7", $"<x>{SandboxGreChannel.RejectMarker}</x>").Value;
        var refused = await simulator.SubmitAsync(Submission(credentials) with { Zip = refusedZip }, CancellationToken.None);
        Assert.Equal(GreSubmitStatus.Refused, refused.Status);
        Assert.Equal("502", refused.ErrorCode);
    }
}
