namespace Sankore.Modules.Leads.Infrastructure.Crypto;

using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Stand-in encryptors for the two contexts that build the M13 model without a DI container:
/// EF's design-time factory and the test context factory.
///
/// <para>
/// <see cref="LeadsDbContext"/> takes an <see cref="IFieldEncryptor"/> because the e-mail column is
/// encrypted through an EF value converter, and a converter is built while the model is built. The
/// design-time factory (<c>dotnet ef migrations add</c>) and the in-memory test factory both
/// construct the context by hand, and neither has the real key — nor needs it: migrations only
/// emit DDL, and a test's ciphertext never leaves the process.
/// </para>
///
/// <para>
/// A SINGLE static instance per purpose, not one per call. EF caches the model per context type and
/// options, so the first converter built wins for every later context sharing those options; a
/// fresh encryptor per construction would make which key a given context actually uses depend on
/// construction order. One instance makes it moot.
/// </para>
/// </summary>
internal static class LeadFieldEncryption
{
    /// <summary>
    /// A fixed, PUBLIC key — it protects nothing and is not a secret. Anything encrypted under it
    /// is either DDL (design time, where no row is read or written) or a test row. Never reachable
    /// from the API: <c>LeadsModule.AddLeadsFieldProtection</c> binds the real key, and the DI
    /// container always resolves that one.
    /// </summary>
    private const string NonSecretKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    internal static readonly IFieldEncryptor Offline =
        new AesGcmFieldEncryptor(Options.Create(new FieldProtectionOptions
        {
            SectionName        = LeadsFieldProtection.SectionName,
            FieldEncryptionKey = NonSecretKey,
            BlindIndexKey      = NonSecretKey,
        }));
}
