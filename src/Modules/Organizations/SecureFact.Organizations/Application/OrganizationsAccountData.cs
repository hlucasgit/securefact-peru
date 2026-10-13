using Microsoft.EntityFrameworkCore;
using SecureFact.Organizations.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Organizations.Application;

/// <summary>The companies of the account and their establishments (ADR-074).</summary>
internal sealed class OrganizationsAccountData(OrganizationsDbContext db) : IAccountDataContributor
{
    private const int Batch = 500;

    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteTableAsync(
            "empresas",
            ["id", "ruc", "razon_social", "nombre_comercial", "domicilio_fiscal", "ubigeo", "regimen", "correo_contacto", "zona_horaria", "moneda", "cuenta_detraccion", "estado", "creada", "actualizada"],
            db.Companies.AsNoTracking().OrderBy(c => c.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(c => (IReadOnlyList<string?>)[c.Id.ToString("D"), c.Ruc, c.LegalName, c.TradeName, c.FiscalAddress, c.Ubigeo, c.TaxRegime, c.ContactEmail, c.TimeZone, c.DefaultCurrency, c.DetractionAccount, c.Status.ToString(), AccountCsv.Stamp(c.CreatedAt), AccountCsv.Stamp(c.UpdatedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "establecimientos",
            ["id", "empresa_id", "codigo", "nombre", "direccion", "ubigeo", "activo", "creado"],
            db.Establishments.AsNoTracking().OrderBy(e => e.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(e => (IReadOnlyList<string?>)[e.Id.ToString("D"), e.CompanyId.ToString("D"), e.Code, e.Name, e.Address, e.Ubigeo, AccountCsv.YesNo(e.IsActive), AccountCsv.Stamp(e.CreatedAt)]),
            cancellationToken);
    }
}
