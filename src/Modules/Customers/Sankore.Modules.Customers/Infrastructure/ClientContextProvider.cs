using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Infrastructure.Workflow;

namespace Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// A client's facts for the circuits on a client record (<c>Account</c>) — what a « Condition »
/// step may read. Statuses and levels only: no protected field (they are encrypted and revealed
/// under audit, not copied into a workflow snapshot).
/// </summary>
internal sealed class ClientContextProvider(CustomersDbContext db) : IContextProvider
{
    public string EntityType => "Account";

    public IReadOnlyList<ContextField> Fields { get; } =
    [
        ContextField.Choice("clientType", "Type de client", ("Individual", "Personne physique"), ("Legal", "Personne morale")),
        ContextField.Choice("status", "Statut du client",
            ("PendingKyc", "KYC en attente"), ("Active", "Actif"), ("Suspended", "Suspendu"),
            ("KycRejected", "KYC refusé"), ("Archived", "Archivé"), ("Merged", "Fusionné")),
        ContextField.Choice("kycStatus", "Statut KYC",
            ("NotStarted", "Non démarré"), ("Pending", "En attente"), ("InProgress", "En cours"),
            ("Approved", "Validé"), ("Rejected", "Refusé"), ("Expired", "Expiré")),
        ContextField.Choice("riskLevel", "Niveau de risque", ("Unknown", "Inconnu"), ("Low", "Faible"), ("Medium", "Moyen"), ("High", "Élevé")),
        ContextField.Text("segment", "Segment"),
        ContextField.Boolean("hasPepExposure", "Exposé politiquement (PPE)"),
    ];

    public async Task<Dictionary<string, object>> BuildAsync(
        Guid entityId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.Id == entityId && c.TenantId == tenantId)
            .Select(c => new { c.Type, c.Status, c.KycStatus, c.RiskLevel, c.SegmentCode, c.HasPepExposure })
            .FirstOrDefaultAsync(cancellationToken);
        if (client is null) return [];

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientType"]     = client.Type.ToString(),
            ["status"]         = client.Status.ToString(),
            ["kycStatus"]      = client.KycStatus.ToString(),
            ["riskLevel"]      = client.RiskLevel.ToString(),
            ["segment"]        = client.SegmentCode ?? string.Empty,
            ["hasPepExposure"] = client.HasPepExposure,
        };
    }
}
