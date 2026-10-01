using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SecureFact.Identity.Application;

/// <summary>RFC 6238 time-based one-time passwords (HMAC-SHA1, 30 s step, 6 digits), compatible with common authenticator apps.</summary>
internal static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 6238 default HMAC-SHA1 is required for interoperability with authenticator apps; HMAC (unlike bare SHA-1) is not affected by collision attacks.")]
    public static string Compute(ReadOnlySpan<byte> secret, long timeStep, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(timeStep & 0xFF);
            timeStep >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>Accepts the current step and one step either side to tolerate clock drift. Returns the matched step to allow replay protection.</summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, int window = 1)
    {
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = now.ToUnixTimeSeconds() / StepSeconds;
        long? match = null;
        for (var step = current - window; step <= current + window; step++)
        {
            var expected = Compute(secret, step);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code)))
            {
                match = step;
            }
        }

        return match;
    }

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 / 5) + 1);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}
