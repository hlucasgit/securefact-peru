using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.Products.Infrastructure;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Products.Application;

/// <summary>The products and services of the account (ADR-074).</summary>
internal sealed class ProductsAccountData(ProductsDbContext db) : IAccountDataContributor
{
    public Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.WriteTableAsync(
            "productos",
            ["id", "codigo_interno", "descripcion", "tipo", "unidad", "valor_unitario", "afectacion_igv", "codigo_sunat", "categoria", "activo", "creado", "actualizado"],
            db.Products.AsNoTracking().OrderBy(p => p.Id).InBatchesAsync(500, cancellationToken)
                .Select(p => (IReadOnlyList<string?>)[p.Id.ToString("D"), p.InternalCode, p.Description, p.Kind.ToString(), p.UnitCode, AccountCsv.Number(p.UnitValue), p.IgvAffectationCode, p.SunatProductCode, p.Category, AccountCsv.YesNo(p.IsActive), AccountCsv.Stamp(p.CreatedAt), AccountCsv.Stamp(p.UpdatedAt)]),
            cancellationToken);
    }
}
