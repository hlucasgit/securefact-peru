using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

/// <summary>Runs something in a platform scope of its own, apart from the one of the caller: the settings of the gateway belong to the platform, and a tenant that pays reads them only through this (ADR-073).</summary>
internal sealed class PlatformAccess(IServiceScopeFactory scopes)
{
    public async Task<T> RunAsync<T>(string reason, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform(reason);
        return await action(scope.ServiceProvider);
    }
}

/// <summary>The gateway as the payments need it: which one, with which keys. The secret key is decrypted here and goes no further than the call to the gateway.</summary>
internal sealed record GatewayRuntime(PaymentGatewayProvider Provider, string? PublicKey, string? SecretKey, bool Enabled)
{
    public string Environment => Provider == PaymentGatewayProvider.Sandbox ? "sandbox" : PublicKey is { } key && key.StartsWith("pk_live_", StringComparison.Ordinal) ? "live" : "test";
}

internal sealed class GatewayAccess(SubscriptionsDbContext db, ISecretProtector secrets)
{
    public const string SecretPurpose = "subscriptions:gateway:secret-key";

    public async Task<GatewayRuntime?> ReadAsync(CancellationToken cancellationToken)
    {
        var setting = await db.GatewaySettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (setting is null)
        {
            return null;
        }

        var secret = setting.ProtectedSecretKey is { } blob ? Encoding.UTF8.GetString(secrets.Unprotect(blob, SecretPurpose)) : null;
        return new GatewayRuntime(setting.Provider, setting.PublicKey, secret, setting.Enabled);
    }
}
