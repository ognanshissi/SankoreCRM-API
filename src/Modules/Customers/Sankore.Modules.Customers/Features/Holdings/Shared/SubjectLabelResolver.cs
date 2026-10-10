namespace Sankore.Modules.Customers.Features.Holdings.Shared;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// Resolves the names behind a set of designation subjects, in ONE pair of queries (BNF-B-15).
///
/// <para>
/// A service and not a per-row lookup, because the worst case is a client's product list: ten
/// holdings with four designations each is forty lookups, and the N+1 would not show up until a
/// client with a real portfolio opened their file.
/// </para>
///
/// <para>
/// Names only. A designation screen has to say who is designated; it has no business returning
/// their date of birth or their document number, and neither of those is read here.
/// </para>
///
/// <para>
/// Public only because <c>HoldingSyncService</c> is — the MassTransit consumers that drive it are
/// instantiated by the broker, and a public type cannot take an internal constructor dependency.
/// Nothing outside this zone has a reason to resolve a designation's label.
/// </para>
/// </summary>
public sealed class SubjectLabelResolver(CustomersDbContext db)
{
    public async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        IEnumerable<BeneficiaryDesignation> designations, CancellationToken ct)
    {
        var clientIds = new List<Guid>();
        var memberIds = new List<Guid>();

        foreach (var designation in designations)
        {
            if (designation.SubjectId is not { } id) continue;

            if (designation.SubjectType == DesignationSubjectType.Client)
                clientIds.Add(id);
            else if (designation.SubjectType == DesignationSubjectType.HouseholdMember)
                memberIds.Add(id);
        }

        var labels = new Dictionary<Guid, string>();

        if (clientIds.Count > 0)
        {
            var clients = await db.Clients
                .Where(c => clientIds.Contains(c.Id))
                .Select(c => new { c.Id, c.DisplayName })
                .ToListAsync(ct);

            foreach (var client in clients)
                labels[client.Id] = client.DisplayName;
        }

        if (memberIds.Count > 0)
        {
            // ExternalFullName and not the structured names: it is kept in step by
            // ClientRelationship.ApplyIdentity precisely so a label needs no reconstruction, and a
            // member who IS a client carries their client's display name there too.
            var members = await db.ClientRelationships
                .Where(r => memberIds.Contains(r.Id))
                .Select(r => new { r.Id, r.ExternalFullName })
                .ToListAsync(ct);

            foreach (var member in members)
            {
                if (!string.IsNullOrWhiteSpace(member.ExternalFullName))
                    labels[member.Id] = member.ExternalFullName;
            }
        }

        return labels;
    }
}
