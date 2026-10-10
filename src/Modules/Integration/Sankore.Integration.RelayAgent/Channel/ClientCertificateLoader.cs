namespace Sankore.Integration.RelayAgent.Channel;

using System.Security.Cryptography.X509Certificates;
using Sankore.Integration.RelayAgent.Configuration;

/// <summary>
/// Loads the client certificate — the agent's identity, and the second half of "configured by a
/// file and a client certificate" (criterion 1).
///
/// <para>
/// Two sources because the two deployment shapes want different ones. A container gets a PKCS#12
/// file mounted in, with its password supplied as a secret file; a Windows service can point at a
/// certificate already in the machine store, where the private key is held by the platform and is
/// never a readable file on disk. The validator refuses both at once and refuses neither — an
/// agent with no client certificate has no mutual TLS, and would fail at the handshake with a
/// message about the platform rather than about itself.
/// </para>
///
/// <para>
/// Loaded ONCE at start-up and kept for the process's life, deliberately: a certificate that the
/// IT team replaces on disk takes effect on the next restart, which is a restart they perform
/// knowingly. Re-reading it per connection would make a half-written file during a renewal into
/// an agent that silently stops reconnecting.
/// </para>
/// </summary>
internal static class ClientCertificateLoader
{
    public static X509Certificate2 Load(RelayCertificateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !string.IsNullOrWhiteSpace(options.StoreThumbprint)
            ? FromStore(options)
            : FromFile(options);
    }

    private static X509Certificate2 FromFile(RelayCertificateOptions options)
    {
        var path = options.Pkcs12Path!;

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Relay:Certificate:Pkcs12Path points at '{path}', which does not exist. "
                + "In Docker this is almost always a missing or mistyped volume mount.");
        }

        var password = ReadPassword(options);

        // EphemeralKeySet keeps the private key in memory instead of letting the platform write it
        // to a key store on disk — the right default for a container, and consistent with
        // criterion 4's "keeps no data beyond the processing in flight".
        //
        // It is not supported everywhere, and the two refusals look nothing alike: macOS throws
        // PlatformNotSupportedException outright ("Remove the flag to allow keys to be temporarily
        // created on disk"), while a Windows CSP-backed key throws CryptographicException. Both are
        // caught, because an agent that would not start on the operator's platform over a storage
        // preference is worse than one that stores the key the ordinary way.
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
                                      or System.Security.Cryptography.CryptographicException)
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
    }

    private static string? ReadPassword(RelayCertificateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PasswordFile))
        {
            if (!File.Exists(options.PasswordFile))
            {
                throw new InvalidOperationException(
                    $"Relay:Certificate:PasswordFile points at '{options.PasswordFile}', which "
                    + "does not exist.");
            }

            // Trimmed: a Docker secret and an `echo` into a file both leave a trailing newline,
            // and a newline in a PKCS#12 password reads as a wrong password.
            return File.ReadAllText(options.PasswordFile).Trim();
        }

        return options.Password;
    }

    private static X509Certificate2 FromStore(RelayCertificateOptions options)
    {
        var location = Enum.Parse<StoreLocation>(options.StoreLocation, ignoreCase: true);
        var name = Enum.TryParse<StoreName>(options.StoreName, ignoreCase: true, out var parsed)
            ? parsed
            : StoreName.My;

        using var store = new X509Store(name, location);
        store.Open(OpenFlags.ReadOnly);

        // Matched on SHA-256 and not with X509FindType.FindByThumbprint: that one matches SHA-1,
        // which is what every Windows UI shows as "the thumbprint" and is also what the enrolment
        // side of this feature does NOT store (IntegrationRelayAgent keeps a SHA-256). Two
        // different hashes under one word is how a certificate "that is clearly installed" is
        // reported as missing.
        foreach (var candidate in store.Certificates)
        {
            var thumbprint = Convert.ToHexStringLower(candidate.GetCertHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256));

            if (string.Equals(thumbprint, options.StoreThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                if (!candidate.HasPrivateKey)
                {
                    throw new InvalidOperationException(
                        "The certificate named by Relay:Certificate:StoreThumbprint has no "
                        + "private key in this store, so it cannot be used for mutual TLS. "
                        + "Import the .pfx, not the .cer.");
                }

                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No certificate with SHA-256 thumbprint '{options.StoreThumbprint}' in "
            + $"{location}\\{name}. Note that the thumbprint Windows displays is SHA-1; obtain "
            + "the SHA-256 one with: "
            + "Get-FileHash -Algorithm SHA256 on the .cer, or "
            + "`certutil -hashfile <file.cer> SHA256`.");
    }
}
