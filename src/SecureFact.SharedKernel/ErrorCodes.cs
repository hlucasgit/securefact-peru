namespace SecureFact.SharedKernel;

/// <summary>Stable error codes exposed to API consumers. Never reuse or renumber a published code.</summary>
public static class ErrorCodes
{
    public const string InvalidRuc = "SF-VAL-001";
    public const string InvalidSeries = "SF-VAL-002";
    public const string InvalidDocumentNumber = "SF-VAL-003";
    public const string InvalidMoney = "SF-VAL-004";
    public const string InvalidRequest = "SF-VAL-005";

    public const string Unauthenticated = "SF-AUTH-001";
    public const string Forbidden = "SF-AUTH-002";
    public const string TenantNotResolved = "SF-AUTH-003";

    public const string Unexpected = "SF-SYS-001";
    public const string RateLimited = "SF-SYS-002";
}
