using System;
using System.Security.Cryptography;
using System.Text;

namespace Yinyue.Services
{
    /// <summary>
    /// Windows implementation of <see cref="ISecretStore"/>: DPAPI, scoped to the current
    /// user, so a config.json copied to another account or machine cannot be read back.
    ///
    /// This is the behaviour ConfigService had inline before the Core extraction, moved
    /// verbatim — including treating a failure as "nothing stored" rather than as an error.
    /// </summary>
    public sealed class DpapiSecretStore : ISecretStore
    {
        public string? Protect(string plaintext)
        {
            try
            {
                byte[] protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext),
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                return Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Secrets] Token protection failed: {ex.Message}");
                return null;
            }
        }

        public string? Unprotect(string protectedValue)
        {
            try
            {
                byte[] unprotectedBytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedValue),
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(unprotectedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Secrets] Token unprotect failed: {ex.Message}");
                return null;
            }
        }
    }
}
