using Microsoft.EntityFrameworkCore;
using SecureFact.Customers.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Customers.Application;

/// <summary>The customers of the account (ADR-074).</summary>
internal sealed class CustomersAccountData(CustomersDbContext db) : IAccountDataContributor
{
    public Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.WriteTableAsync(
            "clientes",
            ["id", "tipo_documento", "documento", "nombre", "direccion", "correo", "telefono", "activo", "creado", "actualizado"],
            db.Customers.AsNoTracking().OrderBy(c => c.Id).InBatchesAsync(500, cancellationToken)
                .Select(c => (IReadOnlyList<string?>)[c.Id.ToString("D"), c.DocumentTypeCode, c.DocumentNumber, c.Name, c.Address, c.Email, c.Phone, AccountCsv.YesNo(c.IsActive), AccountCsv.Stamp(c.CreatedAt), AccountCsv.Stamp(c.UpdatedAt)]),
            cancellationToken);
    }
}
