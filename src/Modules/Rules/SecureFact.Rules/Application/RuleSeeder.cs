using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureFact.Rules.Contracts;
using SecureFact.Rules.Domain;
using SecureFact.Rules.Infrastructure;

namespace SecureFact.Rules.Application;

/// <summary>
/// Loads <c>Seeds/rules.json</c>. A (code, version) that is already loaded must be byte-for-byte equivalent: changing a published rule
/// in place is refused, a change in regulation is a new version with its own effective date (ADR-008).
/// </summary>
internal static class RuleSeeder
{
    public const string ResourceName = "SecureFact.Rules.Seeds.rules.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    internal sealed record SeedRule(string Code, int Version, DateOnly EffectiveFrom, string Source, RuleVerification Verification, JsonElement Configuration);

    internal sealed record SeedFile(string Version, List<SeedRule> Rules);

    internal static SeedFile LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} not found.");
        return JsonSerializer.Deserialize<SeedFile>(stream, Json)
            ?? throw new InvalidDataException("Rule seed is empty.");
    }

    /// <returns>Number of rule versions inserted.</returns>
    public static async Task<int> SeedAsync(RulesDbContext db, CancellationToken cancellationToken, SeedFile? source = null)
    {
        var seed = source ?? LoadEmbedded();
        var inserted = 0;

        foreach (var rule in seed.Rules)
        {
            var configuration = rule.Configuration.GetRawText();
            var existing = await db.RuleVersions.SingleOrDefaultAsync(r => r.Code == rule.Code && r.Version == rule.Version, cancellationToken);
            if (existing is not null)
            {
                if (existing.EffectiveFrom != rule.EffectiveFrom || existing.Source != rule.Source
                    || existing.Verification != rule.Verification || !SameJson(existing.ConfigurationJson, configuration))
                {
                    throw new InvalidOperationException(
                        $"Rule {rule.Code} v{rule.Version} is already loaded with different content. Published rules are immutable: add a new version.");
                }

                continue;
            }

            db.RuleVersions.Add(new RuleVersion
            {
                Id = Guid.CreateVersion7(),
                Code = rule.Code,
                Version = rule.Version,
                EffectiveFrom = rule.EffectiveFrom,
                Source = rule.Source,
                Verification = rule.Verification,
                ConfigurationJson = configuration,
            });
            inserted++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await CloseSupersededVersionsAsync(db, cancellationToken);
        return inserted;
    }

    /// <summary>Each version stays in force until the day before the next one starts.</summary>
    private static async Task CloseSupersededVersionsAsync(RulesDbContext db, CancellationToken cancellationToken)
    {
        var all = await db.RuleVersions.OrderBy(r => r.Code).ThenBy(r => r.EffectiveFrom).ToListAsync(cancellationToken);
        foreach (var group in all.GroupBy(r => r.Code))
        {
            var ordered = group.ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].EffectiveTo = i + 1 < ordered.Count ? ordered[i + 1].EffectiveFrom.AddDays(-1) : null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool SameJson(string a, string b)
    {
        using var left = JsonDocument.Parse(a);
        using var right = JsonDocument.Parse(b);
        return JsonSerializer.Serialize(left.RootElement) == JsonSerializer.Serialize(right.RootElement);
    }
}
