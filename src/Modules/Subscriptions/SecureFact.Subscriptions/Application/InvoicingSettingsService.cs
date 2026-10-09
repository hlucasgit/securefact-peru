using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Subscriptions.Application;

internal sealed class InvoicingSettingsService(
    SubscriptionsDbContext db, IDataScope scope, ICurrentUser actor, TimeProvider clock, IAuditTrail audit, ITenantAdministration tenants, IssuerAccess issuer) : IInvoicingSettings
{
    private const int CompanyPageSize = 100;

    public async Task<Result<InvoicingSettingsDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var setting = await db.InvoicingSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return setting is null
            ? Error.NotFound(ErrorCodes.InvalidInvoicingSettings, "Facturación sin configurar", "La plataforma aún no tiene configurada la cuenta con la que factura.")
            : ToDto(setting);
    }

    public async Task<Result<IReadOnlyList<InvoicingCompanyOption>>> OptionsAsync(Guid issuerTenantId, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var tenant = await tenants.GetAsync(new TenantId(issuerTenantId), cancellationToken);
        if (!tenant.IsSuccess)
        {
            return tenant.Error;
        }

        var options = await issuer.RunAsync(issuerTenantId, async services =>
        {
            var companies = await services.GetRequiredService<ICompanyAdministration>().ListAsync(0, CompanyPageSize, cancellationToken);
            var series = services.GetRequiredService<ISeriesAdministration>();
            var result = new List<InvoicingCompanyOption>();
            foreach (var company in companies.Where(c => c.Status == CompanyStatus.Active))
            {
                var active = (await series.ListAsync(company.Id, cancellationToken)).Where(s => s.IsActive).OrderBy(s => s.Code).Select(s => new InvoicingSeriesOption(s.Id, s.DocumentTypeCode, s.Code)).ToList();
                result.Add(new InvoicingCompanyOption(company.Id, company.Ruc, company.LegalName, active));
            }

            return result;
        });
        return options;
    }

    public async Task<Result<InvoicingSettingsDto>> SetAsync(InvoicingSettingsInput input, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var options = await OptionsAsync(input.IssuerTenantId, cancellationToken);
        if (!options.IsSuccess)
        {
            return options.Error;
        }

        if (options.Value.FirstOrDefault(c => c.Id == input.CompanyId) is not { } company)
        {
            return Invalid("La empresa no es una empresa activa de la cuenta emisora.");
        }

        // The series have to belong to the company and suit what they are for. A credit note of an invoice starts with F and one of a receipt with B (SUNAT's numbering rule for the notes).
        var checks = new (Guid Id, string Type, char Prefix, string Name)[]
        {
            (input.InvoiceSeriesId, "01", 'F', "de facturas"),
            (input.ReceiptSeriesId, "03", 'B', "de boletas de venta"),
            (input.InvoiceNoteSeriesId, "07", 'F', "de notas de crédito de facturas"),
            (input.ReceiptNoteSeriesId, "07", 'B', "de notas de crédito de boletas"),
        };
        foreach (var (id, type, prefix, name) in checks)
        {
            var series = company.Series.FirstOrDefault(s => s.Id == id);
            if (series is null || series.DocumentTypeCode != type || series.Code.Length == 0 || char.ToUpperInvariant(series.Code[0]) != prefix)
            {
                return Invalid($"La serie {name} debe ser una serie activa de la empresa, de tipo {type} y que empiece con {prefix}.");
            }
        }

        var now = clock.GetUtcNow();
        var setting = await db.InvoicingSettings.SingleOrDefaultAsync(cancellationToken);
        if (setting is null)
        {
            setting = InvoicingSetting.Create(input, now);
            db.InvoicingSettings.Add(setting);
        }
        else
        {
            setting.Apply(input, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.InvoicingSettingsSet, "invoicing_settings", null, null,
                NewValues: new Dictionary<string, object?> { ["issuerTenant"] = input.IssuerTenantId, ["company"] = input.CompanyId, ["enabled"] = input.Enabled }),
            cancellationToken);
        return ToDto(setting);
    }

    internal static InvoicingSettingsDto ToDto(InvoicingSetting s) =>
        new(s.IssuerTenantId, s.CompanyId, s.InvoiceSeriesId, s.ReceiptSeriesId, s.InvoiceNoteSeriesId, s.ReceiptNoteSeriesId, s.Enabled, s.UpdatedAt);

    private static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidInvoicingSettings, "Configuración de facturación inválida", detail);

    private Error? RequirePlatformStaff() =>
        scope.Kind == DataScopeKind.Platform && actor.IsPlatform
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo el personal de la plataforma configura con qué cuenta se factura.");
}
