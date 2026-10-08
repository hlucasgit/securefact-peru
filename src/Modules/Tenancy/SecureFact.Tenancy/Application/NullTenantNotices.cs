using SecureFact.Tenancy.Contracts;

namespace SecureFact.Tenancy.Application;

/// <summary>The default when nobody listens: the change happens and no notice goes out.</summary>
internal sealed class NullTenantNotices : ITenantNotices
{
    public Task StatusChangedAsync(TenantStatusNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DomainChangedAsync(DomainNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}
