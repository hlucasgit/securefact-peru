using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureFact.Rules.Contracts;
using SecureFact.Rules.Domain;
using SecureFact.Rules.Infrastructure;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Rules.Application;

internal sealed class RuleProvider(RulesDbContext db) : IRuleProvider
{
    public async Task<Result<RuleVersionDto>> ResolveAsync(string code, DateOnly asOf, CancellationToken cancellationToken)
    {
        var matches = await db.RuleVersions.AsNoTracking()
            .Where(r => r.Code == code && r.EffectiveFrom <= asOf && (r.EffectiveTo == null || r.EffectiveTo >= asOf))
            .ToListAsync(cancellationToken);

        return matches.Count switch
        {
            0 => Error.NotFound(ErrorCodes.RuleNotFound, "Regla no encontrada", $"No hay una versión vigente de la regla '{code}' el {asOf:yyyy-MM-dd}."),
            1 => ToDto(matches[0]),
            // Overlapping versions would silently pick one: refuse instead (a data error, never a guess).
            _ => Error.Conflict(ErrorCodes.RuleInvalid, "Regla ambigua", $"La regla '{code}' tiene {matches.Count} versiones vigentes el {asOf:yyyy-MM-dd}."),
        };
    }

    public async Task<Result<decimal>> ResolveDecimalAsync(string code, string property, DateOnly asOf, CancellationToken cancellationToken)
    {
        var rule = await ResolveAsync(code, asOf, cancellationToken);
        if (!rule.IsSuccess)
        {
            return rule.Error;
        }

        using var doc = JsonDocument.Parse(rule.Value.ConfigurationJson);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : Error.Validation(ErrorCodes.RuleInvalid, "Regla inválida", $"La regla '{code}' no tiene la propiedad numérica '{property}'.");
    }

    public async Task<IReadOnlyList<RuleVersionDto>> ListAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var rows = await db.RuleVersions.AsNoTracking()
            .Where(r => r.EffectiveFrom <= asOf && (r.EffectiveTo == null || r.EffectiveTo >= asOf))
            .OrderBy(r => r.Code)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    internal static RuleVersionDto ToDto(RuleVersion r) =>
        new(r.Code, r.Version, r.EffectiveFrom, r.EffectiveTo, r.Source, r.Verification, r.ConfigurationJson);

}
