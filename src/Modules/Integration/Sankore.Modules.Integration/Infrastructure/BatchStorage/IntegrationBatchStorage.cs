namespace Sankore.Modules.Integration.Infrastructure.BatchStorage;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Names this module's batch-file slot in the object store (INT-24, criterion 5).
///
/// <para>
/// A constant rather than a literal at each call site, for the reason <c>KycObjectStorage</c>
/// states: it is both the DI key of the <see cref="Sankore.Shared.ObjectStorage.IObjectBackend"/>
/// and, on a bucket deployment, the bucket name — and "one bucket per concern" stops being true
/// the moment two spellings of the same word exist.
/// </para>
///
/// <para>
/// Separate from <c>kyc-documents</c> and deliberately so. A batch file is a day's worth of a
/// tenant's outbound customer writes in one object; KYC evidence is per-customer imagery with a
/// different retention, a different key and a different audience. One bucket holding both would
/// make a single leaked credential a leak of both, and would make the purge of criterion 5 —
/// which deletes content a month after acknowledgement — operate in the same keyspace as
/// evidence a regulator may demand years later.
/// </para>
/// </summary>
public static class IntegrationBatchStorage
{
    /// <summary>
    /// The concern this module stores its batch files under. Exposed so the host can declare a
    /// bucket backend under the same key BEFORE the module registers its filesystem fallback —
    /// the module must not choose the medium, because that would mean referencing the S3 client
    /// from an assembly that hosts which store no object also load.
    /// </summary>
    public const string Concern = "integration-batch-files";

    /// <summary>
    /// The filesystem root the host passes to <c>AddObjectBackend</c> as the fallback when no
    /// bucket is configured.
    ///
    /// <para>
    /// Shaped <c>(IConfiguration, IHostEnvironment)</c> to match the bootstrapper's concern list,
    /// which is where every module's root is enumerated — the same shape
    /// <c>KycModule.ResolveObjectStorageBasePath</c> has, and the reason it exists here rather
    /// than being inlined in the host: the default folder name and the setting key belong to this
    /// module, not to whoever happens to compose it.
    /// </para>
    ///
    /// <para>
    /// The environment is accepted and deliberately unused: this module's root depends only on its
    /// own setting, and a fallback that varied by environment would put a deployment's files
    /// somewhere its own logs do not name.
    /// </para>
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configured = config[$"{IntegrationBatchStorageOptions.SectionName}:BasePath"];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, Concern)
            : Path.GetFullPath(configured);
    }
}
