namespace Sankore.Shared.Kernel;

/// <summary>
/// The platform's internal events, by stable code: what a workflow trigger can name and what an
/// outbound webhook can carry. Names, labels and the entity they are about live here once — the
/// Integration module maps each code to its integration event and its payload, the Workflow module
/// lists them in the trigger editor.
///
/// <para>
/// <b>Codes are a contract.</b> Lower-case, dotted, never renamed: triggers and partners store
/// them. <see cref="WorkflowEntityType"/> is the workflow entity a code starts a circuit on
/// (<c>Lead</c>, <c>Opportunity</c> for a project, <c>Account</c> for a client), and the key of
/// the payload that carries its id.
/// </para>
/// </summary>
public static class PlatformEvents
{
    public const string FamilyLead = "Lead";
    public const string FamilyProject = "Projet";
    public const string FamilyClient = "Client";

    /// <summary>The capture event the Leads module publishes itself, before this catalogue existed. Kept: journeys use it.</summary>
    public const string LegacyLeadCaptured = "LEAD_CAPTURED";

    public static IReadOnlyList<PlatformEventDefinition> All { get; } =
    [
        Lead("lead.created", "Lead créé", "Un lead est capturé : saisie, import, formulaire web, webhook entrant ou récupération planifiée."),
        Lead("lead.assigned", "Lead attribué", "Le lead est attribué à un agent (dispatching) ou change de propriétaire."),
        Lead("lead.status_changed", "Statut du lead modifié", "Tout changement de statut. Les transitions qualifié, converti, clôturé et rouvert ont aussi leur propre événement."),
        Lead("lead.stage_changed", "Étape du lead modifiée", "Le lead avance dans les étapes de contact (nouveau, tentative, contact établi, besoin identifié)."),
        Lead("lead.qualified", "Lead qualifié", "Le lead atteint le statut qualifié, avec son score."),
        Lead("lead.converted", "Lead converti en client", "Le lead devient client."),
        Lead("lead.closed", "Lead clôturé", "Le lead est perdu, disqualifié ou archivé. Le motif saisi par l’agent n’est pas transmis."),
        Lead("lead.reopened", "Lead rouvert", "Un lead clôturé est remis au travail."),
        Lead("lead.merged", "Leads fusionnés", "Un lead doublon est fusionné dans un autre et archivé."),
        Lead("lead.consent_withdrawn", "Consentement retiré", "La personne retire un consentement (traitement, prospection, partage…)."),
        // An erased person's record must not start new work: announced to partners only.
        Lead("lead.erased", "Données du lead effacées", "Les données personnelles du lead sont effacées à la demande de la personne : supprimez votre copie.", workflow: false),

        Project("project.created", "Projet créé", "Un projet est ouvert sur un lead ou un client."),
        Project("project.stage_changed", "Étape du projet modifiée", "Le projet passe à une autre étape de son modèle."),
        Project("project.closed", "Projet clôturé", "Le projet est gagné ou perdu."),

        Client("client.created", "Client créé", "Une fiche client est créée, directement ou par conversion d’un lead. Le numéro client n’est pas transmis."),
        Client("client.updated", "Client modifié", "Des informations du client changent. Seuls les NOMS des champs sont transmis, jamais leurs valeurs."),
        Client("client.activated", "Client activé", "Le client devient actif (KYC approuvé, produit sans KYC, ou réactivation)."),
        Client("client.suspended", "Client suspendu", "Le client est suspendu. Le motif saisi n’est pas transmis."),
        Client("client.archived", "Client archivé", "La fiche client est archivée et passe en lecture seule. Le motif saisi n’est pas transmis."),
        Client("client.transferred", "Client transféré", "Le client change d’agence."),
        Client("client.merged", "Clients fusionnés", "Deux fiches client sont fusionnées : reportez vos références de clientId sur survivorClientId."),
        Client("client.segment_changed", "Segment du client modifié", "Le client change de segment."),
        Client("client.kyc_validated", "KYC validé", "Le dossier KYC du client est validé."),
        Client("client.kyc_rejected", "KYC refusé", "Le dossier KYC du client est refusé. Le motif n’est pas transmis."),
        Client("client.anonymized", "Client anonymisé", "Les données personnelles du client sont effacées : supprimez votre copie.", workflow: false),
    ];

    private static readonly Dictionary<string, PlatformEventDefinition> ByCode = All.ToDictionary(e => e.Code, StringComparer.Ordinal);

    public static PlatformEventDefinition Get(string code)
        => ByCode.TryGetValue(code, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown platform event '{code}'.");

    public static PlatformEventDefinition? Find(string? code)
        => code is not null && ByCode.TryGetValue(code, out var definition) ? definition : null;

    private static PlatformEventDefinition Lead(string code, string label, string description, bool workflow = true)
        => new(code, FamilyLead, label, description, "Lead", "leadId", workflow);

    private static PlatformEventDefinition Project(string code, string label, string description, bool workflow = true)
        => new(code, FamilyProject, label, description, "Opportunity", "projectId", workflow);

    private static PlatformEventDefinition Client(string code, string label, string description, bool workflow = true)
        => new(code, FamilyClient, label, description, "Account", "clientId", workflow);
}

/// <param name="WorkflowEntityType">The workflow template entity this event starts a circuit on.</param>
/// <param name="EntityIdKey">The payload key holding that entity's id.</param>
/// <param name="StartsWorkflows">False for erasure notices: they tell partners, they start no work.</param>
public sealed record PlatformEventDefinition(
    string Code,
    string Family,
    string Label,
    string Description,
    string WorkflowEntityType,
    string EntityIdKey,
    bool StartsWorkflows);
