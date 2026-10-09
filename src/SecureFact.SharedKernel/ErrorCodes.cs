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

    public const string TenantInactive = "SF-TEN-002";

    public const string InvalidTenantStatusChange = "SF-TEN-003";

    public const string SuspensionNotYours = "SF-TEN-004";

    public const string PlanLimitReached = "SF-PLAN-001";

    public const string PlanNotFound = "SF-PLAN-002";

    public const string InvalidPlan = "SF-PLAN-003";

    public const string PlanCodeInUse = "SF-PLAN-004";

    public const string ResellerNotFound = "SF-RES-001";

    public const string InvalidReseller = "SF-RES-002";

    public const string ResellerInactive = "SF-RES-003";

    public const string InvalidPrice = "SF-SUB-001";

    public const string InvalidBillingPolicy = "SF-SUB-002";

    public const string ChargeNotFound = "SF-SUB-003";

    public const string InvalidPayment = "SF-SUB-004";

    public const string PaymentNotFound = "SF-SUB-005";

    public const string InvalidCommission = "SF-SUB-006";

    public const string SettlementNotAllowed = "SF-SUB-007";

    public const string InvalidBillingProfile = "SF-SUB-008";

    public const string InvalidInvoicingSettings = "SF-SUB-009";

    public const string ChargeInvoiceFailed = "SF-SUB-010";

    public const string ChargeDocumentNotFound = "SF-SUB-011";

    public const string InvalidBranding = "SF-BRAND-001";

    public const string InvalidLogo = "SF-BRAND-002";

    public const string HostInUse = "SF-BRAND-003";

    public const string InvalidDomain = "SF-DOM-001";

    public const string DomainCheckTooSoon = "SF-DOM-002";

    public const string NoDomain = "SF-DOM-003";

    public const string InvalidSeriesConfiguration = "SF-BIL-001";
    public const string SeriesNotFound = "SF-BIL-002";
    public const string SeriesAlreadyExists = "SF-BIL-003";
    public const string SeriesInactive = "SF-BIL-004";
    public const string SeriesExhausted = "SF-BIL-005";
    public const string InvalidDocument = "SF-BIL-006";
    public const string DocumentNotFound = "SF-BIL-007";
    public const string IdempotencyConflict = "SF-BIL-008";
    public const string DocumentTypeNotSupported = "SF-BIL-009";
    public const string NoteExceedsOriginal = "SF-BIL-010";
    public const string ReferencedDocumentVoided = "SF-BIL-011";

    public const string TaxInvalidInput = "SF-TAX-001";
    public const string TaxUnsupported = "SF-TAX-002";
    public const string TaxPrecisionExceeded = "SF-TAX-003";
    public const string TaxAmountOverflow = "SF-TAX-004";

    public const string CpeInvalidQrField = "SF-CPE-001";
    public const string CpeUnsupported = "SF-CPE-002";
    public const string CpeInvalidDocument = "SF-CPE-003";
    public const string CpeInvalidTransition = "SF-CPE-004";
    public const string CpeChannelNotConfigured = "SF-CPE-005";
    public const string CpeCdrMismatch = "SF-CPE-006";
    public const string CpeNotFound = "SF-CPE-007";
    public const string CpeBusy = "SF-CPE-008";
    public const string CpeNothingToSummarize = "SF-CPE-009";
    public const string CpeReferenceNotAccepted = "SF-CPE-010";
    public const string CpeNotVoidable = "SF-CPE-011";

    public const string InvalidCertificate = "SF-CRT-001";
    public const string CertificateNotFound = "SF-CRT-002";
    public const string CertificateAlreadyExists = "SF-CRT-003";
    public const string CertificateUnavailable = "SF-CRT-004";

    public const string CatalogNotFound = "SF-CAT-001";

    public const string InvalidCustomer = "SF-CUS-001";
    public const string CustomerNotFound = "SF-CUS-002";
    public const string CustomerAlreadyExists = "SF-CUS-003";
    public const string InvalidProduct = "SF-PRD-001";
    public const string ProductNotFound = "SF-PRD-002";
    public const string ProductAlreadyExists = "SF-PRD-003";

    public const string ImportFileInvalid = "SF-IMP-001";
    public const string ImportConflict = "SF-IMP-002";

    public const string InvalidGuide = "SF-GRE-001";
    public const string GuideNotFound = "SF-GRE-002";
    public const string GuideNotSupported = "SF-GRE-003";
    public const string GuideStateInvalid = "SF-GRE-004";
    public const string InvalidGreSeries = "SF-GRE-005";
    public const string GreSeriesNotFound = "SF-GRE-006";
    public const string GreSeriesAlreadyExists = "SF-GRE-007";
    public const string GreChannelNotConfigured = "SF-GRE-008";
    public const string GreBusy = "SF-GRE-009";
    public const string GreSeriesExhausted = "SF-GRE-010";
    public const string GreChannelUnavailable = "SF-GRE-011";

    public const string RuleNotFound = "SF-RUL-001";
    public const string RuleInvalid = "SF-RUL-002";

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
