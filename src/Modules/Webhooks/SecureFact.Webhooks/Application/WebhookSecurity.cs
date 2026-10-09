using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SecureFact.Webhooks.Application;

/// <summary>How the webhooks are configured. Reaching private addresses exists for development and tests only and is refused in production, like the SUNAT simulator.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Allows http and addresses of the machine or the local network. Never in production.</summary>
    public bool AllowLocalTargets { get; set; }

    /// <summary>How many seconds to wait for the answer of an endpoint before the attempt counts as failed.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
}

/// <summary>The signature that goes with each delivery (ADR-067). The receiver recomputes it with its secret and compares in constant time, and refuses a timestamp that is too old.</summary>
public static class WebhookSignature
{
    public const string Version = "v1";

    /// <summary>The header value: <c>v1=</c> and the lower-case hexadecimal HMAC-SHA256, with the secret as key, of <c>{timestamp}.{body}</c>.</summary>
    public static string Sign(string secret, long timestamp, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"{Version}={Convert.ToHexStringLower(hash)}";
    }
}

/// <summary>
/// What a webhook may point at. The platform makes the call, so an address that reaches the inside of its own network (the metadata service of a cloud, a database, another tenant's private host) would
/// turn the webhooks into a way to read it. The check is made twice: on the address when it is registered, and on the numbers it resolves to when the connection is made, which is what stops a name
/// that is changed to point inside after it was approved.
/// </summary>
public static class WebhookTargets
{
    private const int MaxUrlLength = 500;

    /// <summary>The reason an address cannot be registered, or null when it can.</summary>
    public static string? Validate(string? url, bool allowLocal)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return $"La dirección debe ser una URL completa de hasta {MaxUrlLength} caracteres.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "La dirección no lleva usuario, clave ni fragmento.";
        }

        var local = IsLocalName(uri.Host) || (IPAddress.TryParse(uri.IdnHost, out var literal) && !IsPublic(literal));
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowLocal && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "La dirección debe ser https.";
        }

        return local && !allowLocal ? "La dirección apunta a esta máquina o a una red privada: debe ser una dirección pública." : null;
    }

    /// <summary>True for an address of the public internet: not loopback, private, link-local, shared, multicast or reserved, for IPv4 and IPv6 (an IPv4 inside an IPv6 is judged as the IPv4).</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8));
        }

        return false;
    }

    private static bool IsLocalName(string host)
    {
        var name = host.TrimEnd('.').ToLowerInvariant();
        return name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal) || name.EndsWith(".local", StringComparison.Ordinal) || name.EndsWith(".internal", StringComparison.Ordinal) || name.EndsWith(".lan", StringComparison.Ordinal);
    }

    /// <summary>Opens the connection to an endpoint to the addresses that its name resolves to, after checking all of them. Nothing connects to a name that has even one address that is not public.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowLocal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0 || (!allowLocal && addresses.Any(a => !IsPublic(a))))
        {
            throw new HttpRequestException("The address of the endpoint is not public.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
