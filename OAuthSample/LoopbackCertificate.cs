using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OAuthSample
{
    /// <summary>
    /// Finds (or creates and trusts) a self-signed certificate for localhost in the
    /// current user's store. No admin rights or netsh needed. The first time it creates
    /// one, Windows shows a trust-prompt dialog — click Yes. Subsequent runs reuse it.
    /// Shared by the in-process TLS callback listener.
    /// </summary>
    internal static class LoopbackCertificate
    {
        private const string Subject = "CN=OAuthSample Loopback";

        public static X509Certificate2 GetOrCreate(Action<string> log)
        {
            using (var my = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                my.Open(OpenFlags.ReadWrite);

                foreach (var existing in my.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, Subject, false))
                {
                    if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(1))
                    {
                        log("Using existing loopback certificate (" + Subject + ").");
                        return existing;
                    }
                }

                log("Creating a self-signed loopback certificate (" + Subject + ")...");
                using (var rsa = RSA.Create(2048))
                {
                    var req = new CertificateRequest(Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                    var san = new SubjectAlternativeNameBuilder();
                    san.AddDnsName("localhost");
                    san.AddIpAddress(IPAddress.Loopback);
                    san.AddIpAddress(IPAddress.IPv6Loopback);
                    req.CertificateExtensions.Add(san.Build());
                    req.CertificateExtensions.Add(
                        new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

                    using (var ephemeral = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2)))
                    {
                        // Re-import via PFX so the private key is persisted and usable by SslStream.
                        var cert = new X509Certificate2(
                            ephemeral.Export(X509ContentType.Pfx),
                            (string)null,
                            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

                        my.Add(cert);
                        using (var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                        {
                            root.Open(OpenFlags.ReadWrite);
                            root.Add(cert); // one-time Windows trust prompt -> click Yes
                        }

                        log("Certificate created and trusted for the current user.");
                        log("(If Windows shows a security-warning dialog, click Yes to trust it.)");
                        return cert;
                    }
                }
            }
        }
    }
}
