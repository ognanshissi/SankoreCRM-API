namespace Sankore.Modules.Leads.Features.FindDuplicates;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// <see cref="ILeadEmailIndexer"/> over M13's KEYED <see cref="IBlindIndexer"/>.
///
/// <para>
/// The normalisation is the shared one (<c>SensitiveValueNormalizer.NormalizeEmail</c>: trim plus
/// lower-case), which is the same rule <c>MatchProbe.EmailNorm</c> applies — they have to agree, or
/// an address typed with a capital would index to one value and be searched for as another.
/// </para>
///
/// <para>
/// It shares <c>Leads:BlindIndexKey</c> with <see cref="HmacPhoneBlindIndexer"/>. There is no
/// collision — the shared indexer prefixes its input with the purpose, the phone one does not — but
/// the two are coupled for rotation: changing that key invalidates the phone index and the e-mail
/// index together, and both need a backfill.
/// </para>
/// </summary>
internal sealed class LeadEmailIndexer(
    [FromKeyedServices(LeadsFieldProtection.Key)] IBlindIndexer indexer) : ILeadEmailIndexer
{
    public string? Compute(string? email)
        => string.IsNullOrWhiteSpace(email)
            ? null
            : indexer.Compute(BlindIndexPurpose.Email, email);
}
