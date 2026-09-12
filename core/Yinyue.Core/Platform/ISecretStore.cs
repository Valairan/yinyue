using System;

namespace Yinyue.Services
{
    /// <summary>
    /// Protects the Jellyfin access token at rest. DPAPI on Windows, Keychain on macOS.
    /// There is deliberately no shared implementation — a cross-platform secret store would
    /// mean rolling our own key management, which is worse than using each OS's.
    ///
    /// Only ever the token. The password is never persisted in any form.
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>
        /// Returns a protected form of <paramref name="plaintext"/>, or null if the platform
        /// could not protect it. Null means "do not persist", never "persist in the clear".
        /// </summary>
        string? Protect(string plaintext);

        /// <summary>
        /// Reverses <see cref="Protect"/>. Returns null when the value cannot be read back —
        /// typically a config copied from another machine or user account. Callers treat
        /// that as "not signed in" rather than as an error.
        /// </summary>
        string? Unprotect(string protectedValue);
    }

    /// <summary>
    /// The fallback when no platform store has been supplied. It refuses to store anything
    /// rather than storing it unprotected.
    ///
    /// This is the safe direction to fail in: the cost of a store that was never wired up is
    /// that the user signs in again, which is visible and recoverable. The cost of a
    /// plaintext fallback is a token sitting readable in config.json, which is neither.
    /// </summary>
    public sealed class UnavailableSecretStore : ISecretStore
    {
        public static readonly UnavailableSecretStore Instance = new();

        public string? Protect(string plaintext) => null;
        public string? Unprotect(string protectedValue) => null;
    }
}
