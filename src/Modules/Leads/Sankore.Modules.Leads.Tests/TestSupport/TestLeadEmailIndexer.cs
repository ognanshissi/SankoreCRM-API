namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// A REAL HMAC blind indexer over the suite's non-secret key, not a stub that returns the address.
///
/// <para>
/// Dedup by e-mail is an equality test on this value, so a stub that echoed the input would make
/// every test pass while hiding the two things that actually matter: that the same address
/// normalises to the same index whatever its casing, and that a blank one indexes to
/// <c>null</c> rather than to a shared constant that makes every e-mail-less lead a duplicate of
/// every other.
/// </para>
/// </summary>
internal sealed class TestLeadEmailIndexer : ILeadEmailIndexer
{
    private const string NonSecretKey = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";

    internal static readonly ILeadEmailIndexer Instance = new TestLeadEmailIndexer();

    private readonly IBlindIndexer _indexer = new HmacBlindIndexer(
        Options.Create(new FieldProtectionOptions
        {
            SectionName        = "Leads",
            FieldEncryptionKey = NonSecretKey,
            BlindIndexKey      = NonSecretKey,
        }));

    public string? Compute(string? email)
        => string.IsNullOrWhiteSpace(email)
            ? null
            : _indexer.Compute(BlindIndexPurpose.Email, email);
}
