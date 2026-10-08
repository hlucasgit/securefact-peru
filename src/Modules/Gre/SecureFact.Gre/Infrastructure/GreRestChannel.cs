using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureFact.Gre.Contracts;

namespace SecureFact.Gre.Infrastructure;

/// <summary>
/// REST client of SUNAT's GRE platform: a token by the password grant (S28), the send of the zip (a ticket back) and the query of the ticket (S29). The error answers of SUNAT carry
/// <c>cod</c> and <c>msg</c>; a failure of the network, a 5xx and a 429 are transient; the other 4xx refuse the call for good. A rejected authentication is transient too: the person fixes the
/// credentials and sends again, and the guide keeps its number.
/// </summary>
internal sealed class GreRestChannel : IGreChannel
{
    private const int MaxResponseBytes = 40 * 1024 * 1024;
    private const int MaxMessage = 500;
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly GreChannelOptions _options;
    private readonly TimeProvider _clock;
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);

    public GreRestChannel(HttpClient http, GreChannelOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        foreach (var address in new[] { options.SecurityBase, options.ApiBase })
        {
            if (address.Scheme != Uri.UriSchemeHttps && !address.IsLoopback)
            {
                throw new ArgumentException("Las direcciones de SUNAT deben usar HTTPS (solo se admite HTTP en loopback).", nameof(options));
            }
        }

        _http = http;
        _options = options;
        _clock = clock;
    }

    public async Task<GreSubmitOutcome> SubmitAsync(GreSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var path = $"v1/contribuyente/gem/comprobantes/{submission.Credentials.Ruc}-{submission.DocumentTypeCode}-{submission.Series}-{submission.Number.ToString(CultureInfo.InvariantCulture)}";
        var body = new
        {
            archivo = new
            {
                nomArchivo = submission.FileBaseName + ".zip",
                arcGreZip = Convert.ToBase64String(submission.Zip),
                hashZip = Convert.ToHexStringLower(SHA256.HashData(submission.Zip)),
            },
        };

        var reply = await CallAsync(submission.Credentials, HttpMethod.Post, path, JsonContent.Create(body), cancellationToken);
        if (reply.Failure is { } failure)
        {
            return new GreSubmitOutcome(failure.Transient ? GreSubmitStatus.Transient : GreSubmitStatus.Refused, null, failure.Code, failure.Message);
        }

        var ticket = reply.Json!.Value.TryGetProperty("numTicket", out var value) ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(ticket)
            ? new GreSubmitOutcome(GreSubmitStatus.Transient, null, "SF-GRE-NOTICKET", "SUNAT recibió la guía pero no devolvió el número de ticket.")
            : new GreSubmitOutcome(GreSubmitStatus.Received, ticket, null, null);
    }

    public async Task<GreTicketOutcome> QueryTicketAsync(GreChannelCredentials credentials, string ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return new GreTicketOutcome(GreTicketStatus.Error, null, "SF-GRE-NOTICKET", "La guía no tiene ticket.");
        }

        var reply = await CallAsync(credentials, HttpMethod.Get, "v1/contribuyente/gem/comprobantes/envios/" + Uri.EscapeDataString(ticket), null, cancellationToken);
        if (reply.Failure is { } failure)
        {
            return new GreTicketOutcome(failure.Transient ? GreTicketStatus.Transient : GreTicketStatus.Error, null, failure.Code, failure.Message);
        }

        var root = reply.Json!.Value;
        var code = root.TryGetProperty("codRespuesta", out var codeElement) ? codeElement.ToString() : string.Empty;
        var cdr = root.TryGetProperty("arcCdr", out var cdrElement) && cdrElement.ValueKind == JsonValueKind.String ? Decode(cdrElement.GetString()) : null;
        switch (code)
        {
            case "98":
                return new GreTicketOutcome(GreTicketStatus.InProcess, null, null, null);
            case "0":
                return new GreTicketOutcome(GreTicketStatus.Done, cdr, null, null);
            case "99":
                var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object ? errorElement : (JsonElement?)null;
                var number = error is { } e && e.TryGetProperty("numError", out var n) ? n.ToString() : "99";
                var text = error is { } f && f.TryGetProperty("desError", out var d) ? d.GetString() : null;
                return new GreTicketOutcome(GreTicketStatus.Error, cdr, number, Clip(text ?? "SUNAT procesó la guía con error."));
            default:
                return new GreTicketOutcome(GreTicketStatus.Transient, null, "SF-GRE-UNKNOWN", $"SUNAT devolvió un código de respuesta desconocido ({Clip(code)}).");
        }
    }

    private sealed record Failure(bool Transient, string Code, string Message);

    private sealed record Reply(JsonElement? Json, Failure? Failure);

    private async Task<Reply> CallAsync(GreChannelCredentials credentials, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await TokenAsync(credentials, forceNew: attempt > 0, timeout.Token);
                if (token.Failure is not null)
                {
                    return new Reply(null, token.Failure);
                }

                using var request = new HttpRequestMessage(method, new Uri(_options.ApiBase, path)) { Content = content };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    Forget(credentials);
                    continue; // the token may have been revoked before its time: ask for another one, once
                }

                var body = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(false);
                return Interpret(response.StatusCode, body);
            }

            return new Reply(null, new Failure(true, "SF-GRE-AUTH", "SUNAT rechazó el token de acceso."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Reply(null, new Failure(true, "SF-GRE-TIMEOUT", "SUNAT no respondió dentro del tiempo límite."));
        }
        catch (HttpRequestException)
        {
            return new Reply(null, new Failure(true, "SF-GRE-NETWORK", "No se pudo conectar con SUNAT."));
        }
        catch (InvalidDataException)
        {
            return new Reply(null, new Failure(true, "SF-GRE-TOOBIG", "La respuesta de SUNAT excede el tamaño permitido."));
        }
    }

    private sealed record TokenResult(string? Value, Failure? Failure);

    private async Task<TokenResult> TokenAsync(GreChannelCredentials credentials, bool forceNew, CancellationToken cancellationToken)
    {
        var key = TokenKey(credentials);
        var now = _clock.GetUtcNow();
        if (!forceNew && _tokens.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return new TokenResult(cached.Token, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.SecurityBase, $"v1/clientessol/{Uri.EscapeDataString(credentials.ClientId)}/oauth2/token/"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["scope"] = GreChannelOptions.TokenScope,
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["username"] = credentials.Ruc + credentials.SolUser,
                ["password"] = credentials.SolPassword,
            }),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Never echo the body: it can repeat what was sent. A wrong credential is fixed by the person, so the guide keeps its number.
            return new TokenResult(null, new Failure(true, "SF-GRE-AUTH", $"SUNAT no entregó el token de acceso (HTTP {(int)response.StatusCode}). Revise el usuario SOL y las credenciales de API."));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var token = document.RootElement.TryGetProperty("access_token", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                return new TokenResult(null, new Failure(true, "SF-GRE-AUTH", "SUNAT respondió sin token de acceso."));
            }

            var seconds = document.RootElement.TryGetProperty("expires_in", out var life) && life.TryGetInt32(out var parsed) ? parsed : 3600;
            _tokens[key] = (token, now + TimeSpan.FromSeconds(seconds) - TokenMargin);
            return new TokenResult(token, null);
        }
        catch (JsonException)
        {
            return new TokenResult(null, new Failure(true, "SF-GRE-AUTH", "La respuesta del servicio de tokens de SUNAT no es válida."));
        }
    }

    private static void Forget(GreChannelCredentials credentials) => _tokens.TryRemove(TokenKey(credentials), out _);

    /// <summary>The cache key holds a hash of every credential, so a changed password or secret never reuses an old token and no secret stays in memory in clear.</summary>
    private static string TokenKey(GreChannelCredentials c) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{c.Ruc}\n{c.SolUser}\n{c.SolPassword}\n{c.ClientId}\n{c.ClientSecret}")));

    private static Reply Interpret(HttpStatusCode status, byte[] body)
    {
        JsonElement? json = null;
        try
        {
            if (body.Length > 0)
            {
                using var document = JsonDocument.Parse(body);
                json = document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            json = null;
        }

        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            return json is null ? new Reply(null, new Failure(true, "SF-GRE-BADBODY", "La respuesta de SUNAT no es JSON válido.")) : new Reply(json, null);
        }

        var errorCode = json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("cod", out var c) ? c.ToString() : $"HTTP-{code}";
        var message = json is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("msg", out var m) ? m.GetString() : null;
        if (json is { ValueKind: JsonValueKind.Object } detail && detail.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var parts = errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("msg", out var text) ? text.GetString() : null).Where(t => !string.IsNullOrWhiteSpace(t));
            message = string.Join(" ", new[] { message }.Concat(parts).Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        var transient = code >= 500 || code == 429 || code == 408 || code is 401 or 403;
        return new Reply(null, new Failure(transient, errorCode, Clip(string.IsNullOrWhiteSpace(message) ? $"SUNAT respondió HTTP {code}." : message)));
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Clip(string value) => value.Length > MaxMessage ? value[..MaxMessage] : value;

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidDataException("Response too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("Response too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
