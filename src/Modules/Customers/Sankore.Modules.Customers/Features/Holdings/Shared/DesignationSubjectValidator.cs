namespace Sankore.Modules.Customers.Features.Holdings.Shared;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Checks that every designated subject is somebody this holding may actually designate
/// (BNF-B-15).
///
/// <para>
/// <b>Why this is not in the aggregate.</b> <see cref="ProductHolding.Designate"/> validates the
/// SHAPE of a subject — an external person is named and carries no id, anybody else carries one —
/// and that is as far as an aggregate can go: deciding whether a given id is a household member of
/// this client is a query, and an aggregate does not query.
/// </para>
///
/// <para>
/// <b>Why it matters.</b> Without it any non-empty Guid passed as a <c>HouseholdMember</c> or
/// <c>Client</c> subject was accepted and stored. Two consequences, and the second is the serious
/// one: a dangling subject renders as <c>"—"</c> in the clause sent to an insurer — a beneficiary
/// with no name on a death benefit — and an id belonging to another CLIENT, or to another TENANT,
/// would have had that person's name and date of birth read out of the database and carried into
/// the payload. The global query filter does not protect the sync path, which runs filterless by
/// necessity.
/// </para>
/// </summary>
internal sealed class DesignationSubjectValidator(CustomersDbContext db)
{
    /// <summary>
    /// Ok when every subject resolves inside <paramref name="clientId"/>'s own household, or to a
    /// client of the same tenant.
    /// </summary>
    /// <remarks>
    /// Two batched queries at most, and none when the clause names only external persons — which
    /// is the common case for a death benefit.
    ///
    /// <para>
    /// A <c>HouseholdMember</c> must be an ACTIVE member of this client's household. A removed one
    /// is refused rather than accepted-then-flagged: BNF-B-13's <c>ToReview</c> exists for somebody
    /// who left AFTER being designated, which is a different event from designating somebody who
    /// has already gone.
    /// </para>
    ///
    /// <para>
    /// A <c>Client</c> subject may be any client of the tenant, including the holder — a client
    /// legitimately designates their own spouse who banks here too, and BNF-B-15 allows a
    /// co-holder. It may NOT be an archived or merged one: a designation pointing at a record
    /// nobody may transact on is one the insurer cannot pay.
    /// </para>
    /// </remarks>
    internal async Task<Result> EnsureValidAsync(
        Guid tenantId,
        Guid clientId,
        IReadOnlyList<DesignationRequest> requested,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var memberIds = Ids(requested, DesignationSubjectType.HouseholdMember);
        var clientIds = Ids(requested, DesignationSubjectType.Client);

        if (memberIds.Count > 0)
        {
            var valid = await db.ClientRelationships
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId
                         && r.ClientId == clientId
                         && r.ValidTo == null
                         && (r.Type == RelationshipType.Spouse
                          || r.Type == RelationshipType.Child
                          || r.Type == RelationshipType.Dependent)
                         && memberIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct);

            if (valid.Count != memberIds.Count)
                return Result.Fail(CustomerErrors.DesignationSubjectNotInHousehold);
        }

        if (clientIds.Count > 0)
        {
            var valid = await db.Clients
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId
                         && clientIds.Contains(c.Id)
                         && c.Status != ClientStatus.Archived
                         && c.Status != ClientStatus.Merged)
                .Select(c => c.Id)
                .ToListAsync(ct);

            if (valid.Count != clientIds.Count)
                return Result.Fail(CustomerErrors.DesignationSubjectNotFound);
        }

        return Result.Ok();
    }

    /// <summary>
    /// The distinct ids of one subject type. <c>Distinct</c> because the count comparison above is
    /// what detects a missing one, and a clause naming the same member at two ranks would
    /// otherwise never match.
    /// </summary>
    private static List<Guid> Ids(
        IReadOnlyList<DesignationRequest> requested, DesignationSubjectType type)
        => [.. requested
            .Where(r => r.SubjectType == type && r.SubjectId is not null)
            .Select(r => r.SubjectId!.Value)
            .Distinct()];
}
