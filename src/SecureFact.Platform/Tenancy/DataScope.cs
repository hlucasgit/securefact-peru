using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Platform.Tenancy;

public enum DataScopeKind
{
    /// <summary>No tenant resolved. Row Level Security hides every tenant-owned row.</summary>
    Anonymous,

    /// <summary>Operating on behalf of exactly one tenant.</summary>
    Tenant,

    /// <summary>Explicit platform-level operation (backoffice, outbox publisher). Cross-tenant by design and audited.</summary>
    Platform,
}

/// <summary>Data-access scope of the current operation; the single source for the RLS session variables.</summary>
public interface IDataScope : ITenantContext
{
    DataScopeKind Kind { get; }
}

/// <summary>Scoped (per request / per message) holder. Setting the scope is an explicit act, never inferred from client input.</summary>
public sealed class DataScope : IDataScope
{
    public DataScopeKind Kind { get; private set; } = DataScopeKind.Anonymous;

    public TenantId? Current { get; private set; }

    public string? PlatformReason { get; private set; }

    public void UseTenant(TenantId tenantId)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        Kind = DataScopeKind.Tenant;
        Current = tenantId;
        PlatformReason = null;
    }

    public void UsePlatform(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Kind = DataScopeKind.Platform;
        Current = null;
        PlatformReason = reason;
    }

    /// <summary>
    /// Temporarily switches to platform scope for a narrowly-defined internal operation (e.g. looking up an account
    /// during login, before any tenant is known). The previous scope is restored on dispose.
    /// </summary>
    public IDisposable Elevate(string reason)
    {
        var restore = new Restore(this, Kind, Current, PlatformReason);
        UsePlatform(reason);
        return restore;
    }

    public void Clear()
    {
        Kind = DataScopeKind.Anonymous;
        Current = null;
        PlatformReason = null;
    }

    private sealed class Restore(DataScope owner, DataScopeKind kind, TenantId? tenant, string? reason) : IDisposable
    {
        public void Dispose()
        {
            owner.Kind = kind;
            owner.Current = tenant;
            owner.PlatformReason = reason;
        }
    }
}
