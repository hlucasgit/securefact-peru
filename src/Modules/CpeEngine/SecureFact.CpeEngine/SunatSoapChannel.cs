using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.CpeEngine;

/// <summary>
/// SOAP 1.1 client for SUNAT's <c>billService</c>. Envelope shape, namespaces, WS-Security UsernameToken and the
/// <c>getStatus</c> status codes follow the Programmer Manual (§2.2, §2.5, Annex 1). The package travels inline as base64
/// (the manual's example uses a <c>cid:</c> attachment; the parameter type is <c>byte[]</c>) — see R-034.
/// </summary>
internal sealed partial class SunatSoapChannel : ICpeSubmissionChannel
{
    private const string SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ServiceNamespace = "http://service.sunat.gob.pe";
    private const string WsseNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const int MaxResponseBytes = 40 * 1024 * 1024;
    private const int MaxFaultMessage = 500;

    private readonly HttpClient _http;
    private readonly SunatChannelOptions _options;

    public SunatSoapChannel(HttpClient http, SunatChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Endpoint.Scheme != Uri.UriSchemeHttps && !options.Endpoint.IsLoopback)
        {
            throw new ArgumentException("El endpoint de SUNAT debe usar HTTPS (solo se admite HTTP en loopback para simuladores).", nameof(options));
        }

        _http = http;
        _options = options;
    }

    public Task<ChannelReply> SendBillAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default) =>
        SendAsync(credentials, "sendBill", [("fileName", zipFileName), ("contentFile", Convert.ToBase64String(zip))], cancellationToken);

    public Task<ChannelReply> SendSummaryAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default) =>
        SendAsync(credentials, "sendSummary", [("fileName", zipFileName), ("contentFile", Convert.ToBase64String(zip))], cancellationToken);

    public Task<ChannelReply> GetStatusAsync(SunatCredentials credentials, string ticket, CancellationToken cancellationToken = default) =>
        SendAsync(credentials, "getStatus", [("ticket", ticket)], cancellationToken);

    private async Task<ChannelReply> SendAsync(SunatCredentials credentials, string operation, (string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Ruc) || string.IsNullOrWhiteSpace(credentials.SolUser) || string.IsNullOrEmpty(credentials.SolPassword)
            || parameters.Any(p => string.IsNullOrWhiteSpace(p.Value)))
        {
            // The service raises an exception when any parameter is missing (manual §2.5): fail before sending.
            return ChannelReply.Failed(new SunatFault(SunatSide.Client, null, "Faltan credenciales o parámetros obligatorios.", Retryable: false));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(BuildEnvelope(credentials, operation, parameters), Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(false);
            return Interpret(operation, response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelReply.Down("SUNAT no respondió dentro del tiempo límite.");
        }
        catch (HttpRequestException)
        {
            return ChannelReply.Down("No se pudo conectar con SUNAT.");
        }
        catch (InvalidDataException)
        {
            return ChannelReply.Down("La respuesta de SUNAT excede el tamaño permitido.");
        }
    }

    private static string BuildEnvelope(SunatCredentials credentials, string operation, (string Name, string Value)[] parameters)
    {
        XNamespace soap = SoapNamespace;
        XNamespace ser = ServiceNamespace;
        XNamespace wsse = WsseNamespace;

        var call = new XElement(ser + operation, parameters.Select(p => new XElement(p.Name, p.Value)));
        var envelope = new XElement(
            soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", SoapNamespace),
            new XAttribute(XNamespace.Xmlns + "ser", ServiceNamespace),
            new XAttribute(XNamespace.Xmlns + "wsse", WsseNamespace),
            new XElement(
                soap + "Header",
                new XElement(
                    wsse + "Security",
                    new XElement(wsse + "UsernameToken", new XElement(wsse + "Username", credentials.UserName), new XElement(wsse + "Password", credentials.SolPassword)))),
            new XElement(soap + "Body", call));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

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

    private static ChannelReply Interpret(string operation, HttpStatusCode status, byte[] body)
    {
        var bodyElement = ParseBody(body);
        if (bodyElement is null)
        {
            return (int)status is >= 200 and < 300
                ? ChannelReply.Down("La respuesta de SUNAT no es un mensaje SOAP válido.")
                : NonSoapReply(status);
        }

        var fault = bodyElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            return ChannelReply.Failed(ParseFault(fault));
        }

        if ((int)status is < 200 or >= 300)
        {
            return NonSoapReply(status);
        }

        var result = bodyElement.Elements().FirstOrDefault(e => e.Name.LocalName == operation + "Response");
        if (result is null)
        {
            return ChannelReply.Down("La respuesta de SUNAT no tiene el formato esperado.");
        }

        return operation switch
        {
            "sendBill" => FromBase64(Child(result, "applicationResponse")),
            "sendSummary" => Child(result, "ticket") is { Length: > 0 } ticket
                ? ChannelReply.Issued(ticket)
                : ChannelReply.Down("SUNAT no devolvió el ticket."),
            _ => InterpretStatus(result),
        };
    }

    private static ChannelReply InterpretStatus(XElement result)
    {
        var status = result.Descendants().FirstOrDefault(e => e.Name.LocalName == "status");
        var code = status is null ? null : Child(status, "statusCode");
        switch (code)
        {
            case "98":
                return ChannelReply.Processing();
            case "0" or "99":
                // 99 ("proceso con errores") also carries the CDR, which holds the rejection.
                return FromBase64(Child(status!, "content"));
            default:
                return ChannelReply.Down("SUNAT devolvió un estado de ticket desconocido.");
        }
    }

    private static ChannelReply FromBase64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ChannelReply.Down("SUNAT no devolvió el CDR.");
        }

        var buffer = new byte[(text.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(text.Trim(), buffer, out var written)
            ? ChannelReply.Cdr(buffer.AsSpan(0, written).ToArray())
            : ChannelReply.Down("El CDR devuelto por SUNAT no es base64 válido.");
    }

    private static ChannelReply NonSoapReply(HttpStatusCode status)
    {
        var code = (int)status;
        var retryable = code >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
        if (code >= 500)
        {
            return ChannelReply.Down($"SUNAT respondió HTTP {code.ToString(CultureInfo.InvariantCulture)}.");
        }

        var side = code is >= 400 and < 500 ? SunatSide.Client : SunatSide.Unknown;
        return ChannelReply.Failed(new SunatFault(side, null, $"SUNAT respondió HTTP {code.ToString(CultureInfo.InvariantCulture)}.", retryable));
    }

    private static SunatFault ParseFault(XElement fault)
    {
        var faultCode = Child(fault, "faultcode") ?? string.Empty;
        var message = Child(fault, "faultstring") ?? string.Empty;
        if (message.Length > MaxFaultMessage)
        {
            message = message[..MaxFaultMessage];
        }

        var match = FaultCodePattern().Match(faultCode);
        var side = match.Success && match.Groups["side"].Value == "Server" ? SunatSide.Server
            : match.Success && match.Groups["side"].Value == "Client" ? SunatSide.Client
            : SunatSide.Unknown;
        int? code = match.Success && match.Groups["code"].Success
            ? int.Parse(match.Groups["code"].Value, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

        var retryable = side == SunatSide.Server || (code is { } c && SunatCodes.IsRetryable(c));
        return new SunatFault(side, code, message, retryable);
    }

    private static XElement? ParseBody(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(body), settings);
            var root = XDocument.Load(reader).Root;
            return root is { Name.LocalName: "Envelope" } ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "Body") : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    [GeneratedRegex(@"^(?:[\w.-]+:)?(?<side>Server|Client)(?:\.(?<code>\d{1,4}))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex FaultCodePattern();
}
