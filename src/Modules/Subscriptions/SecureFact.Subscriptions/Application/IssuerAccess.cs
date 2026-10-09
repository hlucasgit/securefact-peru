using Microsoft.Extensions.DependencyInjection;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// Runs something as the account of the platform that issues the invoices (ADR-065): a scope of its own, apart from the one of the caller, so what the issuer's modules read and write is the
/// issuer's and the caller's scope (platform staff, a customer) is not touched.
/// </summary>
internal sealed class IssuerAccess(IServiceScopeFactory scopes)
{
    public async Task<T> RunAsync<T>(Guid issuerTenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(issuerTenantId));
        return await action(scope.ServiceProvider);
    }
}
