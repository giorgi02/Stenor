using System.Security.Cryptography;
using System.Text;
using Stenor.Interfaces;

namespace Stenor.Services;

/// <summary>DPAPI (CurrentUser) secret protection; ciphertext is Base64 of the protected
/// bytes, matching the format Stenor has always written to settings.json.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) =>
        Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));

    public string Unprotect(string ciphertext) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), null, DataProtectionScope.CurrentUser));
}
