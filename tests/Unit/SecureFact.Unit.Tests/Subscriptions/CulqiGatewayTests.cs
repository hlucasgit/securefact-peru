using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFact.Subscriptions.Application;

namespace SecureFact.Unit.Tests.Subscriptions;

/// <summary>What the platform does with each answer of the charges API of Culqi (ADR-073): only a paid charge that matches what was asked is a payment; what cannot be told is never a payment nor a refusal.</summary>
public sealed class CulqiGatewayTests
{
    private const string Secret = "sk" + "_test_AbCdEf0123456789";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request);
        }
    }

    private static GatewayCharge Charge(long cents = 11800) =>
        new(Secret, "tkn_test_OK00000000000000", cents, "PEN", "dueno@cliente.test", "SecureFact 2026-11", new Dictionary<string, string> { ["attempt"] = "a1", ["charge"] = "c1" });

    private static (CulqiGateway Gateway, Handler Handler) Gateway(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        return (new CulqiGateway(new HttpClient(handler) { BaseAddress = new Uri("https://api.culqi.com/") }, NullLogger<CulqiGateway>.Instance), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string PaidCharge = """{"object":"charge","id":"chr_test_kEazTaQBDtzNdwFr","amount":11800,"currency_code":"PEN","paid":true,"capture":true,"outcome":{"type":"succesfull_request","code":"succesfull_charge"}}""";

    [Fact]
    public async Task The_request_is_the_one_that_the_reference_describes_with_the_secret_key_only_in_the_header()
    {
        var (gateway, handler) = Gateway(_ => Json(HttpStatusCode.Created, PaidCharge));

        await gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal((HttpMethod.Post, "https://api.culqi.com/v2/charges"), (handler.Seen!.Method, handler.Seen.RequestUri!.ToString()));
        Assert.Equal(("Bearer", Secret), (handler.Seen.Headers.Authorization!.Scheme, handler.Seen.Headers.Authorization.Parameter));
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal(11800, root.GetProperty("amount").GetInt64()); // céntimos, an integer
        Assert.Equal(("PEN", "dueno@cliente.test", "tkn_test_OK00000000000000", true, "SecureFact 2026-11"),
            (root.GetProperty("currency_code").GetString(), root.GetProperty("email").GetString(), root.GetProperty("source_id").GetString(), root.GetProperty("capture").GetBoolean(), root.GetProperty("description").GetString()));
        Assert.Equal("a1", root.GetProperty("metadata").GetProperty("attempt").GetString());
        Assert.DoesNotContain(Secret, handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_paid_charge_of_the_amount_that_was_asked_is_a_payment()
    {
        var (gateway, _) = Gateway(_ => Json(HttpStatusCode.Created, PaidCharge));

        var outcome = await gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal((GatewayStatus.Paid, "chr_test_kEazTaQBDtzNdwFr"), (outcome.Status, outcome.GatewayChargeId));
    }

    [Theory]
    [InlineData("""{"object":"charge","id":"chr_test_kEazTaQBDtzNdwFr","amount":11700,"currency_code":"PEN","paid":true}""")] // another amount
    [InlineData("""{"object":"charge","id":"chr_test_kEazTaQBDtzNdwFr","amount":11800,"currency_code":"USD","paid":true}""")] // another currency
    [InlineData("""{"object":"charge","id":"chr_test_kEazTaQBDtzNdwFr","amount":11800,"currency_code":"PEN","paid":false}""")] // not paid
    [InlineData("""{"object":"token","id":"chr_test_kEazTaQBDtzNdwFr","amount":11800,"currency_code":"PEN","paid":true}""")] // not a charge
    [InlineData("""{"object":"charge","id":"id-raro","amount":11800,"currency_code":"PEN","paid":true}""")] // an id that is not one
    [InlineData("no es json")]
    [InlineData("""["una","lista"]""")]
    public async Task A_201_that_does_not_match_what_was_asked_is_never_taken_for_a_payment_but_goes_to_review(string answer)
    {
        var (gateway, _) = Gateway(_ => Json(HttpStatusCode.Created, answer));

        var outcome = await gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal(GatewayStatus.Unknown, outcome.Status);
    }

    [Fact]
    public async Task A_bank_that_asks_for_authentication_is_told_as_such()
    {
        var (gateway, _) = Gateway(_ => Json(HttpStatusCode.OK, """{"user_message":"El usuario necesita autenticarse","action_code":"REVIEW"}"""));

        var outcome = await gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal((GatewayStatus.NeedsAuthentication, "3ds"), (outcome.Status, outcome.Code));
    }

    [Fact]
    public async Task A_card_error_is_a_refusal_with_the_message_that_is_for_the_customer_and_the_code_of_the_bank()
    {
        var (gateway, _) = Gateway(_ => Json(HttpStatusCode.PaymentRequired,
            """{"object":"error","type":"card_error","charge_id":"chr_test_stm3Fr4HIPC50qxH","code":"DNGE0015","decline_code":"insufficient_funds","merchant_message":"La tarjeta no tiene fondos suficientes.","user_message":"Su tarjeta no tiene fondos suficientes. Verifica tus fondos."}"""));

        var outcome = await gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal((GatewayStatus.Declined, "insufficient_funds", "Su tarjeta no tiene fondos suficientes. Verifica tus fondos."), (outcome.Status, outcome.Code, outcome.Message));
        Assert.DoesNotContain("merchant", outcome.Message, StringComparison.OrdinalIgnoreCase); // what is for the merchant is not shown to the customer
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "gateway-keys")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_amount")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_amount")]
    [InlineData(HttpStatusCode.TooManyRequests, "gateway-busy")]
    public async Task A_request_that_the_gateway_refuses_before_charging_fails_and_says_nothing_of_the_keys(HttpStatusCode status, string code)
    {
        var (gateway, _) = Gateway(_ => Json(status, $$"""{"object":"error","type":"parameter_error","code":"invalid_amount","merchant_message":"Llave {{Secret}} inválida"}"""));

        var outcome = await gateway.ChargeAsync(Charge(), CancellationToken.None);

        Assert.Equal((GatewayStatus.Failed, code), (outcome.Status, outcome.Code));
        Assert.DoesNotContain("sk_", outcome.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task An_answer_of_the_server_that_may_have_charged_goes_to_review(HttpStatusCode status)
    {
        var (gateway, _) = Gateway(_ => Json(status, """{"type":"api_error"}"""));

        Assert.Equal(GatewayStatus.Unknown, (await gateway.ChargeAsync(Charge(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_connection_that_never_happened_charges_nothing_and_one_that_was_lost_may_have()
    {
        var (unreachable, _) = Gateway(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "sin conexión"));
        var (lost, _) = Gateway(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "se cortó"));
        var (timeout, _) = Gateway(_ => throw new TaskCanceledException("tiempo agotado"));

        Assert.Equal(GatewayStatus.Failed, (await unreachable.ChargeAsync(Charge(), CancellationToken.None)).Status);
        Assert.Equal(GatewayStatus.Unknown, (await lost.ChargeAsync(Charge(), CancellationToken.None)).Status);
        Assert.Equal(GatewayStatus.Unknown, (await timeout.ChargeAsync(Charge(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_cancellation_of_the_caller_is_not_taken_for_a_lost_answer()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var (gateway, _) = Gateway(_ => throw new TaskCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.ChargeAsync(Charge(), cancelled.Token));
    }

    [Fact]
    public async Task An_answer_bigger_than_what_a_charge_needs_is_not_trusted()
    {
        var (gateway, _) = Gateway(_ => Json(HttpStatusCode.Created, PaidCharge.Replace("}}", ",\"padding\":\"" + new string('x', 70_000) + "\"}}", StringComparison.Ordinal)));

        Assert.Equal(GatewayStatus.Unknown, (await gateway.ChargeAsync(Charge(), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("OK", "Paid")]
    [InlineData("NOFONDOS", "Declined")]
    [InlineData("3DS", "NeedsAuthentication")]
    [InlineData("CAIDA", "Unknown")]
    [InlineData("ERROR", "Failed")]
    [InlineData("XYZ", "Paid")]
    public async Task The_simulator_does_what_the_token_says_and_charges_nothing(string code, string expected)
    {
        var outcome = await new SandboxGateway().ChargeAsync(Charge() with { TokenId = "tkn_test_" + code.PadRight(16, '0') }, CancellationToken.None);

        Assert.Equal(expected, outcome.Status.ToString());
        if (expected == "Paid")
        {
            Assert.Matches("^chr_test_[A-Za-z0-9]{16}$", outcome.GatewayChargeId);
        }
    }
}
