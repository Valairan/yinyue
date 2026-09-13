using System.Diagnostics;
using Foundation;
using Security;

namespace Yinyue.Services
{
    /// <summary>
    /// macOS implementation of <see cref="ISecretStore"/>: the login Keychain, replacing
    /// DPAPI. Only ever the Jellyfin access token — the password is never persisted in any
    /// form, on either platform.
    ///
    /// The token does not live in config.json here. DPAPI hands back ciphertext that is
    /// meaningful to store in the file; the Keychain stores the secret itself and hands back
    /// a handle. So <see cref="Protect"/> writes to the Keychain and returns a marker, and
    /// config.json carries only that marker. Two consequences worth knowing:
    ///
    ///   · A config.json copied between Macs carries no token, which is the same outcome
    ///     DPAPI gives when a file is copied between Windows accounts.
    ///   · Clearing credentials must delete the Keychain item, not just blank the field.
    ///     <see cref="Unprotect"/> returning null for a missing item is what makes a stale
    ///     marker read as "not signed in" rather than as an error.
    /// </summary>
    public sealed class KeychainSecretStore : ISecretStore
    {
        private const string ServiceName = "com.yinyue.player";
        private const string AccountName = "jellyfin-access-token";

        /// <summary>
        /// What goes into config.json in place of the token. A constant rather than the
        /// secret, so the file stays hand-editable and carries nothing worth stealing.
        /// </summary>
        private const string Marker = "keychain";

        public string? Protect(string plaintext)
        {
            try
            {
                Delete();

                var record = NewRecord();
                record.ValueData = NSData.FromString(plaintext, NSStringEncoding.UTF8);

                // The token is only ever needed while the user is at the machine, and this
                // keeps it out of reach while the Mac is locked.
                record.Accessible = SecAccessible.WhenUnlocked;

                var status = SecKeyChain.Add(record);
                if (status != SecStatusCode.Success)
                {
                    Debug.WriteLine($"[Secrets] Keychain add failed: {status}");
                    return null;
                }

                return Marker;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Secrets] Keychain add threw: {ex.Message}");
                return null;
            }
        }

        public string? Unprotect(string protectedValue)
        {
            try
            {
                var match = SecKeyChain.QueryAsData(NewRecord(), false, out var status);
                if (status != SecStatusCode.Success || match is null)
                {
                    // Missing is the ordinary case after a config copy or a Keychain reset.
                    // Null reads as "not signed in", which is what ConfigService expects.
                    return null;
                }

                return NSString.FromData(match, NSStringEncoding.UTF8)?.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Secrets] Keychain read threw: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Removes the stored token. Blanking the field in config.json is not enough — the
        /// Keychain item would outlive the sign-out and be found again by the next install.
        /// </summary>
        public void Delete()
        {
            try
            {
                SecKeyChain.Remove(NewRecord());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Secrets] Keychain remove threw: {ex.Message}");
            }
        }

        private static SecRecord NewRecord() => new(SecKind.GenericPassword)
        {
            Service = ServiceName,
            Account = AccountName,
        };
    }
}
