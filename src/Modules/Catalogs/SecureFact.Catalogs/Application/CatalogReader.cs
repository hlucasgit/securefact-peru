using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureFact.Catalogs.Contracts;
using SecureFact.Catalogs.Infrastructure;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Catalogs.Application;

internal sealed class CatalogReader(CatalogsDbContext db, TimeProvider clock) : ICatalogReader
{
    public async Task<IReadOnlyList<CatalogSummaryDto>> ListCatalogsAsync(CancellationToken cancellationToken)
    {
        var editions = await db.Editions.AsNoTracking().Where(e => e.EffectiveTo == null).OrderBy(e => e.CatalogNumber).ToListAsync(cancellationToken);
        var counts = await db.Entries.AsNoTracking().Where(e => e.EffectiveTo == null).GroupBy(e => e.CatalogNumber)
            .Select(g => new { Number = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Number, x => x.Count, cancellationToken);
        return editions.Select(e => new CatalogSummaryDto(e.CatalogNumber, e.Name, counts.GetValueOrDefault(e.CatalogNumber), e.Source, e.Version)).ToList();
    }

    public async Task<Result<IReadOnlyList<CatalogEntryDto>>> GetEntriesAsync(string catalogNumber, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var date = asOf ?? Today();
        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.CatalogNumber == catalogNumber && e.Active && e.EffectiveFrom <= date && (e.EffectiveTo == null || e.EffectiveTo >= date))
            .OrderBy(e => e.Code)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0 && !await db.Editions.AnyAsync(e => e.CatalogNumber == catalogNumber, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.CatalogNotFound, "Catálogo no encontrado", $"El catálogo '{catalogNumber}' no existe.");
        }

        IReadOnlyList<CatalogEntryDto> result = rows.Select(e => new CatalogEntryDto(
            e.CatalogNumber, e.Code, e.Description, e.EffectiveFrom, e.EffectiveTo, e.Version, e.Source,
            JsonSerializer.Deserialize<Dictionary<string, string>>(e.MetadataJson) ?? [])).ToList();
        return Result<IReadOnlyList<CatalogEntryDto>>.Success(result);
    }

    public Task<bool> IsValidCodeAsync(string catalogNumber, string code, DateOnly asOf, CancellationToken cancellationToken) =>
        db.Entries.AsNoTracking().AnyAsync(
            e => e.CatalogNumber == catalogNumber && e.Code == code && e.Active && e.EffectiveFrom <= asOf && (e.EffectiveTo == null || e.EffectiveTo >= asOf),
            cancellationToken);

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
