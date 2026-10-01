namespace SecureFact.SharedKernel;

/// <summary>Stable error codes exposed to API consumers. Never reuse or renumber a published code.</summary>
public static class ErrorCodes
{
    public const string InvalidRuc = "SF-VAL-001";
    public const string InvalidSeries = "SF-VAL-002";
    public const string InvalidDocumentNumber = "SF-VAL-003";
    public const string InvalidMoney = "SF-VAL-004";
    public const string InvalidRequest = "SF-VAL-005";
    public const string InvalidTenantName = "SF-VAL-006";

    public const string TenantNotFound = "SF-TEN-001";

    public const string InvalidSeriesConfiguration = "SF-BIL-001";
    public const string SeriesNotFound = "SF-BIL-002";
    public const string SeriesAlreadyExists = "SF-BIL-003";
    public const string SeriesInactive = "SF-BIL-004";
    public const string SeriesExhausted = "SF-BIL-005";
    public const string InvalidDocument = "SF-BIL-006";
    public const string DocumentNotFound = "SF-BIL-007";
    public const string IdempotencyConflict = "SF-BIL-008";
    public const string DocumentTypeNotSupported = "SF-BIL-009";

    public const string TaxInvalidInput = "SF-TAX-001";
    public const string TaxUnsupported = "SF-TAX-002";
    public const string TaxPrecisionExceeded = "SF-TAX-003";
    public const string TaxAmountOverflow = "SF-TAX-004";

    public const string CpeInvalidQrField = "SF-CPE-001";

    public const string CatalogNotFound = "SF-CAT-001";

    public const string InvalidCompany = "SF-ORG-001";
    public const string CompanyNotFound = "SF-ORG-002";
    public const string CompanyAlreadyExists = "SF-ORG-003";
    public const string InvalidEstablishment = "SF-ORG-004";
    public const string EstablishmentNotFound = "SF-ORG-005";
    public const string EstablishmentAlreadyExists = "SF-ORG-006";

    public const string Unauthenticated = "SF-AUTH-001";
    public const string Forbidden = "SF-AUTH-002";
    public const string TenantNotResolved = "SF-AUTH-003";
    public const string InvalidCredentials = "SF-AUTH-004";
    public const string MfaRequired = "SF-AUTH-005";
    public const string InvalidMfaCode = "SF-AUTH-006";
    public const string InvalidRefreshToken = "SF-AUTH-007";
    public const string WeakPassword = "SF-AUTH-008";
    public const string InvalidResetToken = "SF-AUTH-009";

    public const string InvalidEmail = "SF-USR-001";
    public const string EmailInUse = "SF-USR-002";
    public const string UserNotFound = "SF-USR-003";
    public const string RoleNotAssignable = "SF-USR-004";
    public const string UnknownRole = "SF-USR-005";

    public const string Unexpected = "SF-SYS-001";
    public const string RateLimited = "SF-SYS-002";
}
