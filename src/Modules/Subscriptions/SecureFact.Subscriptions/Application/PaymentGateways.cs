using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Subscriptions.Application;

/// <summary>Where the card payments are allowed to be simulated (ADR-073): the simulator charges nothing and is refused in production.</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Lets the platform choose the simulator as its gateway. Never in production.</summary>
    public bool AllowSandbox { get; set; }
}

internal enum GatewayStatus
{
    Paid,
    Declined,
    NeedsAuthentication,

    /// <summary>The request was refused before any money moved (bad keys, bad data, the gateway is busy).</summary>
    Failed,

    /// <summary>The gateway may have charged the card and the answer did not arrive or was not understood: it is reviewed by a person, never tried again by itself.</summary>
    Unknown,
}

/// <param name="Metadata">Facts that the gateway keeps with the charge, so a charge found there points at the attempt that made it. Never a secret.</param>
internal sealed record GatewayCharge(string SecretKey, string TokenId, long AmountCents, string Currency, string Email, string Description, IReadOnlyDictionary<string, string> Metadata);

internal sealed record GatewayOutcome(GatewayStatus Status, string? GatewayChargeId, string? Code, string? Message);

/// <summary>A gateway that charges the card that a token stands for (the card itself never reaches the platform).</summary>
internal interface IPaymentGateway
{
    PaymentGatewayProvider Provider { get; }

    Task<GatewayOutcome> ChargeAsync(GatewayCharge charge, CancellationToken cancellationToken);
}

/// <summary>
/// Culqi (ADR-073), through its charges API as its reference describes it: <c>POST /v2/charges</c> with the secret key as bearer token. 201 with a paid charge is a payment; 200 with
/// <c>action_code</c> REVIEW asks for 3D Secure; 402 is the bank refusing. What cannot be told from the answer is never taken for a payment nor for a refusal.
/// </summary>
internal sealed partial class CulqiGateway(HttpClient http, ILogger<CulqiGateway> logger) : IPaymentGateway
{
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxMessageLength = 300;

    public PaymentGatewayProvider Provider => PaymentGatewayProvider.Culqi;

    public async Task<GatewayOutcome> ChargeAsync(GatewayCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);
        var body = new Dictionary<string, object?>
        {
            ["amount"] = charge.AmountCents,
            ["currency_code"] = charge.Currency,
            ["email"] = charge.Email,
            ["source_id"] = charge.TokenId,
            ["capture"] = true,
            ["description"] = charge.Description,
            ["metadata"] = charge.Metadata,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/charges") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", charge.SecretKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure) when (failure.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            // It never reached the gateway: nothing was charged.
            LogUnreachable(logger, failure.HttpRequestError.ToString());
            return new GatewayOutcome(GatewayStatus.Failed, null, "unreachable", "No se pudo conectar con la pasarela de pago. Intente de nuevo en unos minutos.");
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The request may have arrived: whether the card was charged is not known.
            LogNoAnswer(logger, failure.GetType().Name);
            return new GatewayOutcome(GatewayStatus.Unknown, null, "no-answer", "La pasarela no respondió y no se sabe si el pago se hizo.");
        }

        using (response)
        {
            var text = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            return Interpret(response.StatusCode, text, charge);
        }
    }

    private GatewayOutcome Interpret(HttpStatusCode status, string? text, GatewayCharge charge)
    {
        JsonDocument? document = null;
        try
        {
            document = text is null ? null : JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // an answer that is not JSON is read below as what it is: nothing that can be trusted
        }

        using (document)
        {
            var root = document?.RootElement;
            var gatewayId = root is { ValueKind: JsonValueKind.Object } o && Text(o, "id") is { } id && ChargeId().IsMatch(id) ? id : null;
            switch ((int)status)
            {
                case 201 when root is { ValueKind: JsonValueKind.Object } charged:
                    return Text(charged, "object") == "charge" && gatewayId is not null && charged.TryGetProperty("paid", out var paid) && paid.ValueKind == JsonValueKind.True
                        && charged.TryGetProperty("amount", out var amount) && amount.TryGetInt64(out var cents) && cents == charge.AmountCents && Text(charged, "currency_code") == charge.Currency
                        ? new GatewayOutcome(GatewayStatus.Paid, gatewayId, null, null)
                        : new GatewayOutcome(GatewayStatus.Unknown, gatewayId, "unexpected-charge", "La pasarela respondió algo que no coincide con el pago pedido.");
                case 200 when root is { ValueKind: JsonValueKind.Object } review && Text(review, "action_code") == "REVIEW":
                    return new GatewayOutcome(GatewayStatus.NeedsAuthentication, null, "3ds", "Su banco pide una autenticación adicional (3D Secure) para esta tarjeta.");
                case 402 when root is { ValueKind: JsonValueKind.Object } refused && Text(refused, "type") == "card_error":
                    return new GatewayOutcome(GatewayStatus.Declined, Text(refused, "charge_id") is { } declined && ChargeId().IsMatch(declined) ? declined : null, Text(refused, "decline_code") ?? Text(refused, "code") ?? "declined", Limit(Text(refused, "user_message")) ?? "El banco rechazó el pago.");
                case 401:
                    LogRefused(logger, (int)status, Text(root, "type"), Text(root, "code"));
                    return new GatewayOutcome(GatewayStatus.Failed, null, "gateway-keys", "La pasarela de pago no está bien configurada. Avise a quien le da el servicio.");
                case 400 or 404 or 422:
                    LogRefused(logger, (int)status, Text(root, "type"), Text(root, "code"));
                    return new GatewayOutcome(GatewayStatus.Failed, null, Text(root, "code") ?? "gateway-rejected", "La pasarela rechazó la solicitud de pago. Vuelva a intentarlo con otra tarjeta o avise a quien le da el servicio.");
                case 429:
                    return new GatewayOutcome(GatewayStatus.Failed, null, "gateway-busy", "La pasarela está ocupada. Intente de nuevo en unos minutos.");
                default:
                    LogUnexpected(logger, (int)status);
                    return new GatewayOutcome(GatewayStatus.Unknown, gatewayId, "gateway-unexpected", "La pasarela respondió algo inesperado y no se sabe si el pago se hizo.");
            }
        }
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Limit(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;

    /// <summary>The body, at most <see cref="MaxResponseBytes"/>: a gateway that answers with more is not trusted with memory.</summary>
    private static async Task<string?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [GeneratedRegex("^chr_(test|live)_[A-Za-z0-9]{16}$")]
    private static partial Regex ChargeId();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The payment gateway could not be reached ({Failure}); nothing was charged.")]
    private static partial void LogUnreachable(ILogger logger, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "The payment gateway did not answer ({Failure}); the charge is under review.")]
    private static partial void LogNoAnswer(ILogger logger, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "The payment gateway refused the request: HTTP {Status}, type {Type}, code {Code}.")]
    private static partial void LogRefused(ILogger logger, int status, string? type, string? code);

    [LoggerMessage(Level = LogLevel.Error, Message = "The payment gateway answered HTTP {Status}, which is not understood; the charge is under review.")]
    private static partial void LogUnexpected(ILogger logger, int status);
}

/// <summary>
/// The simulator of the gateway for development and tests (ADR-073): it charges nothing. The token says what happens, by the first characters after <c>tkn_test_</c>: <c>OK</c> pays, <c>NOFONDOS</c>
/// is refused, <c>3DS</c> asks for authentication, <c>CAIDA</c> loses the answer (under review), <c>ERROR</c> fails before any charge; anything else pays.
/// </summary>
internal sealed class SandboxGateway : IPaymentGateway
{
    public PaymentGatewayProvider Provider => PaymentGatewayProvider.Sandbox;

    public Task<GatewayOutcome> ChargeAsync(GatewayCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);
        var code = charge.TokenId.Length > 9 ? charge.TokenId[9..] : string.Empty;
        var id = "chr_test_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 16);
        GatewayOutcome outcome = code switch
        {
            _ when code.StartsWith("NOFONDOS", StringComparison.Ordinal) => new GatewayOutcome(GatewayStatus.Declined, id, "insufficient_funds", "Su tarjeta no tiene fondos suficientes. Verifique sus fondos o intente con otra tarjeta."),
            _ when code.StartsWith("3DS", StringComparison.Ordinal) => new GatewayOutcome(GatewayStatus.NeedsAuthentication, null, "3ds", "Su banco pide una autenticación adicional (3D Secure) para esta tarjeta."),
            _ when code.StartsWith("CAIDA", StringComparison.Ordinal) => new GatewayOutcome(GatewayStatus.Unknown, null, "no-answer", "La pasarela no respondió y no se sabe si el pago se hizo."),
            _ when code.StartsWith("ERROR", StringComparison.Ordinal) => new GatewayOutcome(GatewayStatus.Failed, null, "gateway-rejected", "La pasarela rechazó la solicitud de pago."),
            _ => new GatewayOutcome(GatewayStatus.Paid, id, null, null),
        };
        return Task.FromResult(outcome);
    }
}
