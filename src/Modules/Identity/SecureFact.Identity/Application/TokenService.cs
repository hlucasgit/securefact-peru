using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SecureFact.Identity.Domain;

namespace SecureFact.Identity.Application;

/// <summary>Issues short-lived signed access tokens and opaque refresh tokens. Only the SHA-256 of a refresh token is ever stored.</summary>
internal sealed class TokenService(IOptions<IdentityOptions> options, TimeProvider clock)
{
    public const string SessionClaim = "sid";
    public const string TenantClaim = "tid";
    public const string RoleClaim = "role";

    private readonly JsonWebTokenHandler _handler = new();

    public static byte[] HashRefreshToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public (string Token, int ExpiresInSeconds) IssueAccessToken(User user, Guid sessionId)
    {
        var opts = options.Value;
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(opts.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString("D"),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            [SessionClaim] = sessionId.ToString("D"),
            [RoleClaim] = user.Roles.Select(r => r.RoleCode).ToArray(),
        };
        if (user.TenantId is { } tenantId)
        {
            claims[TenantClaim] = tenantId.ToString("D");
        }

        var key = new SymmetricSecurityKey(Convert.FromBase64String(opts.SigningKey)) { KeyId = opts.SigningKeyId };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = opts.Issuer,
            Audience = opts.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + lifetime).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return (_handler.CreateToken(descriptor), (int)lifetime.TotalSeconds);
    }

    /// <summary>Validation parameters shared with the API host so issuer and verifier can never diverge.</summary>
    public static TokenValidationParameters ValidationParameters(IdentityOptions opts)
    {
        var keys = new List<SecurityKey> { new SymmetricSecurityKey(Convert.FromBase64String(opts.SigningKey)) { KeyId = opts.SigningKeyId } };
        if (!string.IsNullOrEmpty(opts.PreviousSigningKey) && !string.IsNullOrEmpty(opts.PreviousSigningKeyId))
        {
            keys.Add(new SymmetricSecurityKey(Convert.FromBase64String(opts.PreviousSigningKey)) { KeyId = opts.PreviousSigningKeyId });
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opts.Issuer,
            ValidateAudience = true,
            ValidAudience = opts.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }
}
