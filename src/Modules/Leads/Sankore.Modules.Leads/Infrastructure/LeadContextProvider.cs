using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Workflow;

namespace Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Hydrates the workflow context dictionary with Lead-specific fields so that
/// transition conditions and step rules can evaluate values like Score or Status
/// without the Workflow module knowing about the Leads domain.
/// No contact value and no free text: a condition reads facts, and the context snapshot is stored.
/// </summary>
internal sealed class LeadContextProvider(LeadsDbContext db) : IContextProvider
{
    public string EntityType => "Lead";

    public IReadOnlyList<ContextField> Fields => LeadContextFields.All;

    public async Task<Dictionary<string, object>> BuildAsync(
        Guid entityId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var lead = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.Id == entityId && l.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return lead is null ? [] : LeadContextFields.Of(lead);
    }
}

/// <summary>The lead's facts a condition may read — shared by the lead and the project providers.</summary>
internal static class LeadContextFields
{
    public static readonly IReadOnlyList<ContextField> All =
    [
        ContextField.Choice("status", "Statut du lead",
            ("New", "Nouveau"), ("Open", "Contacté"), ("Qualifying", "En qualification"), ("Qualified", "Qualifié"),
            ("Nurturing", "En maturation"), ("Recycled", "Recyclé"), ("Converted", "Converti"), ("Lost", "Perdu"),
            ("Disqualified", "Disqualifié"), ("Archived", "Archivé")),
        ContextField.Number("score", "Score du lead"),
        ContextField.Choice("intentLevel", "Intention",
            ("Unknown", "Inconnue"), ("Cold", "Froid"), ("Warm", "Tiède"), ("Hot", "Chaud")),
        ContextField.Choice("source", "Source du lead",
            ("Web", "Site web"), ("MobileAgent", "Agent mobile"), ("Agency", "Agence"), ("CallCenter", "Centre d'appels"),
            ("Sms", "SMS"), ("Ussd", "USSD"), ("WhatsApp", "WhatsApp"), ("Referral", "Recommandation"),
            ("Partner", "Partenaire"), ("FileImport", "Import"), ("Campaign", "Campagne")),
        ContextField.Text("interestedProduct", "Produit demandé"),
        ContextField.Number("desiredAmount", "Montant souhaité"),
        ContextField.Choice("kind", "Type de lead", ("NewProspect", "Nouveau prospect"), ("ExistingCustomer", "Client existant")),
        ContextField.Boolean("hasEmail", "A une adresse e-mail"),
        ContextField.Boolean("hasPepExposure", "Exposé politiquement (PPE)"),
        ContextField.Number("qualificationCompleteness", "Complétude de la qualification (%)"),
        ContextField.Text("preferredLanguage", "Langue préférée"),
    ];

    public static Dictionary<string, object> Of(Lead lead, string prefix = "")
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [prefix + "status"]                    = lead.Status.ToString(),
            [prefix + "score"]                     = (double)lead.Score,
            [prefix + "intentLevel"]               = lead.IntentLevel.ToString(),
            [prefix + "source"]                    = lead.Source.ToString(),
            [prefix + "interestedProduct"]         = lead.InterestedProduct ?? string.Empty,
            [prefix + "desiredAmount"]             = (double)(lead.DesiredAmount?.Amount ?? 0m),
            [prefix + "kind"]                      = lead.Kind.ToString(),
            [prefix + "hasEmail"]                  = !string.IsNullOrWhiteSpace(lead.Email),
            [prefix + "hasPepExposure"]            = lead.HasPepExposure,
            [prefix + "qualificationCompleteness"] = lead.QualificationCompleteness,
            [prefix + "preferredLanguage"]         = lead.PreferredLanguage ?? string.Empty,
        };
}

/// <summary>
/// A project's facts for its circuits — the project's own, plus its lead's under « lead. ».
/// </summary>
internal sealed class ProjectContextProvider(LeadsDbContext db) : IContextProvider
{
    public string EntityType => "Opportunity";

    public IReadOnlyList<ContextField> Fields { get; } =
    [
        ContextField.Text("product", "Produit du projet"),
        ContextField.Number("estimatedAmount", "Montant estimé"),
        ContextField.Number("probability", "Probabilité (%)"),
        ContextField.Choice("stage", "Phase du projet",
            ("Prospecting", "Prospection"), ("Qualification", "Qualification"), ("NeedsAnalysis", "Analyse du besoin"),
            ("Proposal", "Proposition"), ("Negotiation", "Négociation"), ("ClosedWon", "Gagné"), ("ClosedLost", "Perdu")),
        ContextField.Boolean("hasClient", "Ouvert pour un client"),
        .. LeadContextFields.All.Select(f => f with { Key = "lead." + f.Key, Label = f.Label.Replace(" du lead", "") + " (lead)" }),
    ];

    public async Task<Dictionary<string, object>> BuildAsync(
        Guid entityId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var project = await db.Opportunities
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == entityId && o.TenantId == tenantId, cancellationToken);
        if (project is null) return [];

        var context = project.LeadId is { } leadId
            && await db.Leads.IgnoreQueryFilters()
                .FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId, cancellationToken) is { } lead
            ? LeadContextFields.Of(lead, "lead.")
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        context["product"]         = project.Product ?? string.Empty;
        context["estimatedAmount"] = (double)(project.EstimatedAmount?.Amount ?? 0m);
        context["probability"]     = project.Probability;
        context["stage"]           = project.Stage.ToString();
        context["hasClient"]       = project.CustomerEntityId.HasValue;
        return context;
    }
}
