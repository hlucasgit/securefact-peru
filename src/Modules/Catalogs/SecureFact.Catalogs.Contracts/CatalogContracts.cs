using SecureFact.SharedKernel.Results;

namespace SecureFact.Catalogs.Contracts;

public sealed record CatalogSummaryDto(string Number, string Name, int EntryCount, string Source, int Version);

/// <summary>One code of an official catalogue valid for a date range (ADR-008: <c>EffectiveFrom</c>/<c>EffectiveTo</c>/<c>Version</c>/<c>Source</c>).</summary>
public sealed record CatalogEntryDto(
    string CatalogNumber,
    string Code,
    string Description,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    int Version,
    string Source,
    IReadOnlyDictionary<string, string> Metadata);

public interface ICatalogReader
{
    Task<IReadOnlyList<CatalogSummaryDto>> ListCatalogsAsync(CancellationToken cancellationToken);

    /// <summary>Entries valid on <paramref name="asOf"/> (today when null).</summary>
    Task<Result<IReadOnlyList<CatalogEntryDto>>> GetEntriesAsync(string catalogNumber, DateOnly? asOf, CancellationToken cancellationToken);

    Task<bool> IsValidCodeAsync(string catalogNumber, string code, DateOnly asOf, CancellationToken cancellationToken);
}
