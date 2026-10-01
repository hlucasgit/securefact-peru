using SecureFact.Rules.Contracts;

namespace SecureFact.Rules.Domain;

internal sealed class RuleVersion
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string ConfigurationJson { get; set; } = "{}";

    public string Source { get; set; } = string.Empty;

    public RuleVerification Verification { get; set; }
}
