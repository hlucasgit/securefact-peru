namespace SecureFact.Platform.Security;

/// <summary>
/// Envelope encryption boundary for secrets at rest (certificates, SOL keys, TOTP seeds, integration tokens; ADR-007).
/// <paramref name="purpose"/> is bound to the ciphertext, so a value protected for one purpose cannot be decrypted for another.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose);
}
