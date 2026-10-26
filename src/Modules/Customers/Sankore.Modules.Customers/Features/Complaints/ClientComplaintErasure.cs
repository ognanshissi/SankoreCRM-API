namespace Sankore.Modules.Customers.Features.Complaints;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// A client erased (retention or a person's request): their complaints keep counting in the
/// register, emptied of what they said and were told. Shared by both erasure paths so neither
/// forgets it. The caller saves.
/// </summary>
internal static class ClientComplaintErasure
{
    public static async Task AnonymizeAsync(CustomersDbContext db, Guid tenantId, Guid clientId, DateTimeOffset now, CancellationToken ct)
    {
        var complaints = await db.ClientComplaints
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId && c.ClientId == clientId && c.AnonymizedAt == null)
            .ToListAsync(ct);

        foreach (var complaint in complaints) complaint.Anonymize(now);
    }
}
