using SecureFact.SharedKernel.Results;

namespace SecureFact.Rules.Contracts;

/// <summary>Codes of the regulatory rules served by the provider. Add a constant here when a rule is introduced.</summary>
public static class RuleCodes
{
    public const string IgvRate = "tax.igv.rate";
    public const string IgvReducedRate = "tax.igv.reduced_rate";
    public const string IvapRate = "tax.ivap.rate";
    public const string IcbperUnitAmount = "tax.icbper.unit_amount";
    public const string IssueDateMaxAgeDays = "billing.issue_date_max_age_days";
    public const string NoteIssueDateMaxAgeDays = "billing.note_issue_date_max_age_days";

    /// <summary>Amount in soles above which a receipt (or a note of one) must identify the buyer.</summary>
    public const string ReceiptIdentificationThreshold = "billing.receipt_identification_threshold";
}

public enum RuleVerification
{
    /// <summary>The value was read in an official primary source.</summary>
    Verified,

    /// <summary>Plausible value awaiting confirmation; <c>Source</c> says what to check.</summary>
    Pending,
}

public sealed record RuleVersionDto(
    string Code,
    int Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string Source,
    RuleVerification Verification,
    string ConfigurationJson);

/// <summary>
/// Regulatory rules as versioned data (ADR-008): exactly one version of a rule is in force on a given date, and documents are always
/// evaluated with the version in force on their tax date.
/// </summary>
public interface IRuleProvider
{
    Task<Result<RuleVersionDto>> ResolveAsync(string code, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>Resolves the rule and reads one numeric property of its configuration (e.g. <c>rate</c>).</summary>
    Task<Result<decimal>> ResolveDecimalAsync(string code, string property, DateOnly asOf, CancellationToken cancellationToken);

    Task<IReadOnlyList<RuleVersionDto>> ListAsync(DateOnly asOf, CancellationToken cancellationToken);
}
