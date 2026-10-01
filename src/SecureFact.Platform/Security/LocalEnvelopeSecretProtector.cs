using System.Security.Cryptography;
using System.Text;

namespace SecureFact.Platform.Security;

/// <summary>
/// AES-256-GCM envelope encryption with a key-encryption key (KEK) held in process memory.
/// Local development and tests only: production must plug a KMS/Vault-backed <see cref="ISecretProtector"/> (ADR-007).
/// Layout: version(1) | wrapNonce(12) | wrapTag(16) | wrappedDek(32) | dataNonce(12) | dataTag(16) | ciphertext.
/// </summary>
public sealed class LocalEnvelopeSecretProtector : ISecretProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 1 + NonceSize + TagSize + KeySize + NonceSize + TagSize;
    private static readonly byte[] WrapAad = Encoding.UTF8.GetBytes("securefact:dek-wrap:v1");

    private readonly byte[] _kek;

    public LocalEnvelopeSecretProtector(ReadOnlySpan<byte> kek)
    {
        if (kek.Length != KeySize)
        {
            throw new ArgumentException($"The key-encryption key must be exactly {KeySize} bytes.", nameof(kek));
        }

        _kek = kek.ToArray();
    }

    public static LocalEnvelopeSecretProtector FromBase64(string? base64Kek)
    {
        if (string.IsNullOrWhiteSpace(base64Kek))
        {
            throw new InvalidOperationException("SF_LOCAL_DEV_KEK is not configured. Generate one with: openssl rand -base64 32");
        }

        return new LocalEnvelopeSecretProtector(Convert.FromBase64String(base64Kek));
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        var output = new byte[HeaderSize + plaintext.Length];
        var span = output.AsSpan();
        span[0] = Version;
        var wrapNonce = span.Slice(1, NonceSize);
        var wrapTag = span.Slice(1 + NonceSize, TagSize);
        var wrappedDek = span.Slice(1 + NonceSize + TagSize, KeySize);
        var dataNonce = span.Slice(1 + NonceSize + TagSize + KeySize, NonceSize);
        var dataTag = span.Slice(1 + (2 * NonceSize) + TagSize + KeySize, TagSize);
        var ciphertext = span[HeaderSize..];

        var dek = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            RandomNumberGenerator.Fill(wrapNonce);
            using (var wrap = new AesGcm(_kek, TagSize))
            {
                wrap.Encrypt(wrapNonce, dek, wrappedDek, wrapTag, WrapAad);
            }

            RandomNumberGenerator.Fill(dataNonce);
            using var data = new AesGcm(dek, TagSize);
            data.Encrypt(dataNonce, plaintext, ciphertext, dataTag, Encoding.UTF8.GetBytes(purpose));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        if (protectedData.Length < HeaderSize || protectedData[0] != Version)
        {
            throw new CryptographicException("Protected data has an unknown format.");
        }

        var wrapNonce = protectedData.Slice(1, NonceSize);
        var wrapTag = protectedData.Slice(1 + NonceSize, TagSize);
        var wrappedDek = protectedData.Slice(1 + NonceSize + TagSize, KeySize);
        var dataNonce = protectedData.Slice(1 + NonceSize + TagSize + KeySize, NonceSize);
        var dataTag = protectedData.Slice(1 + (2 * NonceSize) + TagSize + KeySize, TagSize);
        var ciphertext = protectedData[HeaderSize..];

        var dek = new byte[KeySize];
        try
        {
            using (var wrap = new AesGcm(_kek, TagSize))
            {
                wrap.Decrypt(wrapNonce, wrappedDek, wrapTag, dek, WrapAad);
            }

            var plaintext = new byte[ciphertext.Length];
            using var data = new AesGcm(dek, TagSize);
            data.Decrypt(dataNonce, ciphertext, dataTag, plaintext, Encoding.UTF8.GetBytes(purpose));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
