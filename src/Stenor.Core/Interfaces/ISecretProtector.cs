namespace Stenor.Interfaces;

/// <summary>
/// Encrypts the API key for storage at rest. Implemented in Stenor.App with DPAPI
/// (CurrentUser); ciphertext is an opaque string the implementation can round-trip.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>. Throws when the ciphertext cannot be decrypted
    /// (e.g. a different user profile); callers treat that as "no key".</summary>
    string Unprotect(string ciphertext);
}
