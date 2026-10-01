using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecureFact.Identity.Application;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Tenancy;
using Microsoft.IdentityModel.Tokens;

namespace SecureFact.Identity;

public static class IdentityModule
{
    /// <summary>
    /// Registers the module. Requires <c>AddPlatformDataScope</c>, an <see cref="SecureFact.Platform.Security.ISecretProtector"/>,
    /// an <see cref="ICurrentUser"/> and an <see cref="IPasswordResetNotifier"/> to be registered by the host.
    /// </summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, string appConnectionString, Action<IdentityOptions> configure)
    {
        services.AddOptions<IdentityOptions>().Configure(configure).Validate(IsValid, "Identity options are invalid: the signing key must be base64 of at least 32 bytes.");
        services.AddDbContext<IdentityDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<TokenService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<IPlatformBootstrapper, PlatformBootstrapper>();
        return services;
    }

    /// <summary>Validation parameters the API host uses to verify access tokens (same source as the issuer).</summary>
    public static TokenValidationParameters AccessTokenValidationParameters(IdentityOptions options) => TokenService.ValidationParameters(options);

    public static string SessionClaim => TokenService.SessionClaim;

    public static string TenantClaim => TokenService.TenantClaim;

    public static string RoleClaim => TokenService.RoleClaim;

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .Options;
        await using var db = new IdentityDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static bool IsValid(IdentityOptions options)
    {
        try
        {
            return Convert.FromBase64String(options.SigningKey).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
