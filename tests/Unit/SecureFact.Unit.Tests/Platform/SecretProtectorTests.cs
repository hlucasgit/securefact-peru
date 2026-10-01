using System.Security.Cryptography;
using System.Text;
using SecureFact.Platform.Security;

namespace SecureFact.Unit.Tests.Platform;

public class SecretProtectorTests
{
    private static LocalEnvelopeSecretProtector NewProtector() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_trip_returns_the_original_bytes()
    {
        var protector = NewProtector();
        var secret = Encoding.UTF8.GetBytes("clave-sol-de-prueba");

        var blob = protector.Protect(secret, "sol-key");

        Assert.Equal(secret, protector.Unprotect(blob, "sol-key"));
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext_and_is_randomized()
    {
        var protector = NewProtector();
        var secret = Encoding.UTF8.GetBytes("super-secret-value");

        var first = protector.Protect(secret, "p");
        var second = protector.Protect(secret, "p");

        Assert.NotEqual(first, second);
        Assert.False(first.AsSpan().IndexOf(secret) >= 0);
    }

    [Fact]
    public void A_value_cannot_be_decrypted_for_a_different_purpose()
    {
        var protector = NewProtector();
        var blob = protector.Protect("x"u8, "totp-seed");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, "sol-key"));
    }

    [Fact]
    public void Tampered_data_is_rejected()
    {
        var protector = NewProtector();
        var blob = protector.Protect("payload"u8, "p");
        blob[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, "p"));
    }

    [Fact]
    public void A_different_kek_cannot_decrypt()
    {
        var blob = NewProtector().Protect("payload"u8, "p");

        Assert.ThrowsAny<CryptographicException>(() => NewProtector().Unprotect(blob, "p"));
    }

    [Fact]
    public void Kek_must_be_32_bytes() =>
        Assert.Throws<ArgumentException>(() => new LocalEnvelopeSecretProtector(new byte[16]));
}
