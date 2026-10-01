using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SecureFact.Identity.Application;

/// <summary>PBKDF2-HMAC-SHA512 with a per-password salt. Format: <c>pbkdf2-sha512$iterations$salt$hash</c> (base64).</summary>
internal sealed class PasswordHasher(IOptions<IdentityOptions> options)
{
    private const string Prefix = "pbkdf2-sha512";
    private const int SaltSize = 16;
    private const int HashSize = 64;

    private readonly Lazy<string> _dummy = new(() => Hash("dummy-password-for-timing-equalisation", options.Value.Pbkdf2Iterations));

    public string Hash(string password) => Hash(password, options.Value.Pbkdf2Iterations);

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Burns the same CPU as a real verification so unknown accounts are not distinguishable by timing.</summary>
    public void VerifyDummy(string password) => Verify(password, _dummy.Value);

    private static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HashSize);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}
