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
    private const string Combined = "SecureFact";

    public static IServiceCollection AddSecureFactAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<SecureFact.SharedKernel.Tenancy.ICurrentUser>(sp =>
            new HttpCurrentUser(() => sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User));

        // One scheme in front of two: a call with an API key (ADR-066) goes to the key handler, any other to the token of a person.
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = Combined;
                options.DefaultChallengeScheme = Combined;
            })
            .AddPolicyScheme(Combined, "API key or access token", options =>
                options.ForwardDefaultSelector = context => ApiKeyAuthenticationHandler.Presented(context.Request) is not null ? ApiKeyAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
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
        else if (user.ResellerId is { } reseller && user.TenantId is null)
        {
            // A reseller has no tenant of its own and reaches the tenants of its customers through the platform scope. Nothing else is open to it: its role has only the reseller permissions,
            // and the reseller services filter every read and write by the reseller of the token (ADR-043).
            scope.UsePlatform($"reseller:{reseller}:{user.UserId}");
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

        // A reseller that was switched off is refused on every request.
        if (user.ResellerId is { } activeReseller
            && !await services.GetRequiredService<IResellerAdministration>().IsActiveAsync(activeReseller, context.HttpContext.RequestAborted))
        {
            context.Fail("The reseller is not active.");
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

