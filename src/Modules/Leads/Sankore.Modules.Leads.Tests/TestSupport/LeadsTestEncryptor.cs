namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// The encryptor every in-memory <c>LeadsDbContext</c> in this suite is built with.
///
/// <para>
/// ONE static instance, not one per context. EF caches the model per context type and options, so
/// the converter built first wins for every later context sharing those options — a fresh
/// encryptor per factory call would make which key a context actually uses depend on construction
/// order, and the symptom would be an occasional failed decrypt in whichever test ran second.
/// </para>
/// </summary>
internal static class LeadsTestEncryptor
{
    private const string NonSecretKey = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";

    internal static readonly IFieldEncryptor Instance =
        new AesGcmFieldEncryptor(Options.Create(new FieldProtectionOptions
        {
            SectionName        = "Leads",
            FieldEncryptionKey = NonSecretKey,
            BlindIndexKey      = NonSecretKey,
        }));
}
