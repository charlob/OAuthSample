using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace OAuthSample
{
    /// <summary>A persisted OAuth session — enough to renew silently or show who's signed in.</summary>
    public sealed class TokenRecord
    {
        public string Authority { get; set; }
        public string ClientId { get; set; }
        public string RefreshToken { get; set; }
        public string AccessToken { get; set; }
        public string IdToken { get; set; }
        public string UserName { get; set; }
        public long ExpiresAtUnix { get; set; } // seconds since epoch; 0 = unknown

        [ScriptIgnore]
        public DateTimeOffset ExpiresAt =>
            ExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix) : DateTimeOffset.MinValue;

        [ScriptIgnore]
        public bool HasRefreshToken => !string.IsNullOrEmpty(RefreshToken);
    }

    /// <summary>
    /// Stores a <see cref="TokenRecord"/> on disk, encrypted with Windows DPAPI scoped to
    /// the current user (<see cref="DataProtectionScope.CurrentUser"/>) — so a refresh token
    /// (a long-lived bearer credential) is never written in plaintext and can't be read by
    /// another user on the machine. This is the desktop equivalent of a secure token cache;
    /// a "cookie" would be the wrong tool (that's a browser session concept).
    /// </summary>
    public sealed class TokenStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OAuthSample", "session.dat");

        // Extra entropy mixed into DPAPI — not a secret, just app-specific salting.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OAuthSample.session.v1");

        public TokenRecord Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;
                byte[] encrypted = File.ReadAllBytes(FilePath);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(plain);
                return new JavaScriptSerializer().Deserialize<TokenRecord>(json);
            }
            catch
            {
                return null; // missing/corrupt/undecryptable → treat as no saved session
            }
        }

        public void Save(TokenRecord record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string json = new JavaScriptSerializer().Serialize(record);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, encrypted);
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch
            {
                // ignore
            }
        }
    }
}
