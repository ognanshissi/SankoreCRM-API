namespace Sankore.Modules.Kyc.Features.RemoteInvitations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;

/// <summary>
/// Withdraws the link in force on a file when something makes it wrong to keep (KRM-B-04).
///
/// <para>
/// Three things do: the customer's email changes (the link was posted to the old address), their
/// date of birth changes (the identification question no longer has the answer the customer was
/// given), and the file leaves collection through the back office (an agent has taken over, and the
/// customer uploading in parallel would race the validator).
/// </para>
///
/// <para>
/// <b>A service and not three copies, because the motive is the part that matters.</b> The
/// revocation itself is one aggregate call; what an agent reads afterwards is <c>RevokeReason</c>,
/// and three call sites writing their own French sentence is how one of them ends up saying
/// nothing useful. The reasons are constants here.
/// </para>
///
/// <para>
/// <b>It never saves.</b> Every caller is inside a transaction that is about to commit something
/// else — a verification starting, a sensitive field changing — and the revocation belongs to that
/// unit of work: a link withdrawn without the change that justified it, or a change committed with
/// a live link still pointing at the old state, are both worse than either alone. The caller's
/// <c>SaveChangesAsync</c> is what persists this.
/// </para>
/// </summary>
internal sealed class KycRemoteInvitationRevoker(
    KycDbContext db,
    TimeProvider clock,
    ILogger<KycRemoteInvitationRevoker> logger)
{
    /// <summary>The customer's address changed, so the link went somewhere they no longer read.</summary>
    internal const string EmailChangedReason =
        "Adresse email du client modifiée — le lien a été envoyé à l'ancienne adresse";

    /// <summary>
    /// The date of birth changed, so the question the customer was told to answer no longer has
    /// the answer they were given. Leaving the link live would lock them out after five tries.
    /// </summary>
    internal const string BirthDateChangedReason =
        "Date de naissance du client corrigée — l'identification du lien ne correspond plus";

    /// <summary>The file is being handled in the back office; a parallel upload would race it.</summary>
    internal const string FileLeftCollectionReason =
        "Dossier repris en agence — la collecte à distance n'est plus attendue";

    /// <summary>
    /// Revokes the active link of <paramref name="kycFileId"/>, if there is one, and answers
    /// whether it did. Nothing to revoke is the ordinary case — most files have no link at all.
    /// </summary>
    internal async Task<bool> RevokeActiveAsync(
        Guid tenantId, Guid kycFileId, string reason, Guid? revokedBy, CancellationToken ct)
    {
        // A List and not the static array: on .NET 10 an array's Contains binds to the
        // ReadOnlySpan<T> extension and no longer translates to SQL.
        var activeStatuses = KycRemoteInvitation.ActiveStatuses.ToList();

        // IgnoreQueryFilters with an explicit tenant predicate: one caller is a MassTransit
        // consumer with no ambient tenant of its own.
        var active = await db.KycRemoteInvitations
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(i => i.TenantId == tenantId
                     && i.KycFileId == kycFileId
                     && activeStatuses.Contains(i.Status))
            .FirstOrDefaultAsync(ct);

        if (active is null)
            return false;

        var revoked = active.Revoke(reason, revokedBy, clock);

        if (revoked.IsFailure)
        {
            // The only failure Revoke has is a blank reason, and every reason here is a constant.
            // Logged rather than thrown because no caller of this service should fail over it.
            logger.LogError(
                "Could not revoke collection link {InvitationId}: {Error}",
                active.Id, revoked.Error);
            return false;
        }

        logger.LogInformation(
            "Collection link {InvitationId} of KYC file {KycFileId} revoked: {Reason}",
            active.Id, kycFileId, reason);

        return true;
    }
}
