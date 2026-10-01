namespace SecureFact.Identity.Application;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public string Issuer { get; set; } = "securefact";

    public string Audience { get; set; } = "securefact-api";

    /// <summary>Id of the current signing key, written to the <c>kid</c> header so keys can be rotated.</summary>
    public string SigningKeyId { get; set; } = "k1";

    /// <summary>Base64, at least 32 bytes. Comes from a secret store / environment, never from source control.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Previous key kept only to validate tokens issued before a rotation.</summary>
    public string? PreviousSigningKeyId { get; set; }

    public string? PreviousSigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 10;

    public int RefreshTokenDays { get; set; } = 14;

    public int SessionAbsoluteDays { get; set; } = 30;

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int PasswordResetMinutes { get; set; } = 30;

    /// <summary>PBKDF2-HMAC-SHA512 iterations (OWASP recommends 210,000 or more). Tests lower it for speed only.</summary>
    public int Pbkdf2Iterations { get; set; } = 210_000;

    public string TotpIssuer { get; set; } = "SecureFact";
}
