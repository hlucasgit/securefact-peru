using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Api.Infrastructure;

/// <summary>Request metadata for audit and diagnostics, read from the current HTTP request.</summary>
public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    private HttpContext? Http => accessor.HttpContext;

    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Truncate(Http?.Request.Headers.UserAgent.ToString(), 300);

    public string? CorrelationId => Http?.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id) == true ? id as string : null;

    public string? RequestId => Http?.TraceIdentifier;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length > max ? value[..max] : value;
}
