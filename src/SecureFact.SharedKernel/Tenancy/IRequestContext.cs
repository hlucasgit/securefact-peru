namespace SecureFact.SharedKernel.Tenancy;

/// <summary>Technical context of the current request, used for audit and diagnostics. Absent (null members) outside HTTP, e.g. in workers.</summary>
public interface IRequestContext
{
    string? IpAddress { get; }

    string? UserAgent { get; }

    string? CorrelationId { get; }

    string? RequestId { get; }
}
