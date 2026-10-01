namespace Sankore.Shared.ObjectStorage;

using Microsoft.Extensions.Configuration;

/// <summary>
/// The credentials and endpoint of the bucket provider, read from <c>ObjectStorage:R2</c>.
///
/// <para>
/// These are <em>platform-level deployment secrets</em>: one account, one pair of keys, for the
/// whole installation. They belong in configuration — user-secrets locally, environment variables
/// in a deployment — next to <c>Kyc:Storage:EncryptionKey</c>, which already protects the very
/// same documents and arrives the same way.
/// </para>
///
/// <para>
/// They deliberately do NOT go through <c>ISecretsModule</c>. That vault is keyed
/// <c>(TenantId, Scope, EntityId, Name)</c> and reads through a scoped <c>DbContext</c>, so it
/// cannot serve a singleton built at start-up with a credential that belongs to no tenant — and
/// bootstrapping it would mean the database must be reachable before the object store exists,
/// which inverts the dependency for a migration whose whole job is to move files.
/// </para>
///
/// <para>
/// Every failure here names the configuration key to set, following
/// <c>KycDocumentStore.ReadKey</c>. This repo has a documented history of a missing key surfacing
/// as <c>ArgumentNullException (Parameter 's')</c> from <c>Convert.FromBase64String</c> on the
/// first write — a 500 for whoever happened to save a document, with nothing in it naming the
/// setting. A storage credential fails at construction, by name, or not at all.
/// </para>
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>The configuration section. A colon path, so it reads the same in a key name as in an error message.</summary>
    public const string SectionName = "ObjectStorage:R2";

    /// <summary>Cloudflare account id; the service URL is derived from it unless one is given.</summary>
    public string? AccountId { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// Full endpoint override. Empty in a normal R2 deployment — it exists for an S3-compatible
    /// endpoint that is not R2 (MinIO in a test rig, another provider) and for a jurisdiction-
    /// specific R2 endpoint, neither of which can be derived from an account id.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// <c>true</c> when the deployment said anything at all about a bucket.
    ///
    /// <para>
    /// "Anything at all", not "everything needed", on purpose: a section that is half filled in is
    /// a mistake, not a choice, and must fail the boot rather than fall back. Falling back would
    /// write KYC evidence to a container filesystem that the next redeploy discards, and nothing
    /// would report it until a regulator asked for a document that no longer exists.
    /// </para>
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId)
        || !string.IsNullOrWhiteSpace(AccessKeyId)
        || !string.IsNullOrWhiteSpace(SecretAccessKey)
        || !string.IsNullOrWhiteSpace(ServiceUrl);

    /// <summary>
    /// Reads the section by hand rather than binding it.
    ///
    /// <para>
    /// <c>Microsoft.Extensions.Configuration.Binder</c> is not referenced here (this project keeps
    /// its dependency surface small so the S3 client reaches only the projects that store an
    /// object), and binding would in any case hand back an object whose blank properties say
    /// nothing about which key was missing.
    /// </para>
    /// </summary>
    public static ObjectStorageOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        return new ObjectStorageOptions
        {
            AccountId = Trimmed(section["AccountId"]),
            AccessKeyId = Trimmed(section["AccessKeyId"]),
            SecretAccessKey = Trimmed(section["SecretAccessKey"]),
            ServiceUrl = Trimmed(section["ServiceUrl"]),
        };
    }

    /// <summary>
    /// The endpoint the S3 client is pointed at: <see cref="ServiceUrl"/> when given, otherwise
    /// R2's per-account host. Call <see cref="Validate"/> first — this assumes a valid pair.
    /// </summary>
    public string ResolveServiceUrl() =>
        string.IsNullOrWhiteSpace(ServiceUrl)
            ? $"https://{AccountId}.r2.cloudflarestorage.com"
            : ServiceUrl!.TrimEnd('/');

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming the configuration key at fault.
    /// Called once, at registration, so a bad credential stops a deployment instead of surfacing
    /// as a failed upload hours later.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId) && string.IsNullOrWhiteSpace(ServiceUrl))
            throw Missing(
                $"{SectionName}:AccountId",
                "the Cloudflare account id the R2 endpoint is derived from "
                + $"(or set {SectionName}:ServiceUrl to a full S3-compatible endpoint instead)");

        if (string.IsNullOrWhiteSpace(AccessKeyId))
            throw Missing($"{SectionName}:AccessKeyId", "the R2 API token's access key id");

        if (string.IsNullOrWhiteSpace(SecretAccessKey))
            throw Missing($"{SectionName}:SecretAccessKey", "the R2 API token's secret access key");

        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw Malformed(
                    $"{SectionName}:ServiceUrl",
                    $"'{ServiceUrl}' is not an absolute http(s) URL, e.g. "
                    + "'https://<accountid>.r2.cloudflarestorage.com'");
        }
        else
        {
            // A full URL pasted into AccountId would otherwise derive
            // "https://https://x.r2.cloudflarestorage.com/.r2.cloudflarestorage.com", whose DNS
            // failure names neither setting.
            foreach (var c in AccountId!)
            {
                if (char.IsAsciiLetterOrDigit(c) || c == '-') continue;

                throw Malformed(
                    $"{SectionName}:AccountId",
                    $"'{AccountId}' is not an account id (letters, digits and '-' only). "
                    + $"A full endpoint belongs in {SectionName}:ServiceUrl.");
            }
        }
    }

    private static InvalidOperationException Missing(string key, string what) =>
        new($"{key} is not configured ({what}). Set it with: "
            + $"dotnet user-secrets set \"{key}\" \"<value>\" --project src/Bootstrapper/Sankore.Api");

    private static InvalidOperationException Malformed(string key, string why) =>
        new($"{key} is malformed: {why}");

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
