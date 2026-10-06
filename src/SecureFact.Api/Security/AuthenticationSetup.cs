using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using SecureFact.Identity;
using SecureFact.Identity.Application;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Security;

public static class AuthenticationSetup
{
    public static IServiceCollection AddSecureFactAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<SecureFact.SharedKernel.Tenancy.ICurrentUser>(sp =>
            new HttpCurrentUser(() => sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentityOptions>>((jwt, identity) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = IdentityModule.AccessTokenValidationParameters(identity.Value);
                jwt.Events = new JwtBearerEvents { OnTokenValidated = BindScopeAndCheckSessionAsync };
            });

        services.AddAuthorization(options =>
        {
            // Secure by default: every endpoint requires an authenticated user unless it opts out with AllowAnonymous.
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => RoleCatalog
                        .PermissionsOf(ctx.User.FindAll(IdentityModule.RoleClaim).Select(c => c.Value))
                        .Contains(permission)));
            }
        });
        return services;
    }

    private static async Task BindScopeAndCheckSessionAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var scope = services.GetRequiredService<DataScope>();
        // HttpContext.User is assigned only after this event, so read the principal that was just validated.
        var user = new HttpCurrentUser(() => context.Principal);

        // The data scope is derived exclusively from the signed token: tenant users get their tenant, platform staff the platform scope.
        if (user.TenantId is { } tenant && !user.IsPlatform)
        {
            scope.UseTenant(tenant);
        }
        else if (user.IsPlatform && user.TenantId is null)
        {
            scope.UsePlatform($"platform-staff:{user.UserId}");
        }
        else
        {
            context.Fail("The token does not identify a valid scope.");
            return;
        }

        if (user.SessionId is not { } sessionId
            || !await services.GetRequiredService<IAuthenticationService>().IsSessionActiveAsync(sessionId, context.HttpContext.RequestAborted))
        {
            context.Fail("The session is no longer active.");
            return;
        }

        // A suspended or closed tenant is refused on every request, not only at sign-in (the status is cached for a few seconds, ITenantStatusReader).
        if (user.TenantId is { } tenantId && !user.IsPlatform
            && await services.GetRequiredService<ITenantStatusReader>().GetStatusAsync(tenantId, fresh: false, context.HttpContext.RequestAborted) != TenantStatus.Active)
        {
            context.Fail("The tenant is not active.");
        }
    }
}

