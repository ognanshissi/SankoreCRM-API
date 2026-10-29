namespace Sankore.Modules.Integration.Features.Webhooks;

using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Kernel;

/// <summary>
/// What an outbound webhook may announce: a stable code a partner subscribes to, the integration
/// event it comes from, and the <c>data</c> it carries.
///
/// <para>
/// <b>The projection is the contract, not the event.</b> An integration event is an internal
/// message and may gain a field tomorrow; a webhook payload is read by somebody else's code. Each
/// entry therefore names its keys one by one, and a new field reaches partners only when it is
/// added here — with a version bump if an existing key changes meaning.
/// </para>
///
/// <para>
/// <b>No personal value and no free text, ever</b> (product decision pending on an opt-in for
/// leads — see the webhooks plan). Identifiers, enum names, amounts and dates only. Two fields are
/// left out on purpose although their events carry them: the client NUMBER, which is the secret
/// the remote KYC journey checks a customer against, and every operator-typed motive (suspension,
/// archiving, KYC refusal, loss detail).
/// </para>
/// </summary>
internal static class WebhookEventCatalog
{
    public const string FamilyLead = PlatformEvents.FamilyLead;
    public const string FamilyProject = PlatformEvents.FamilyProject;
    public const string FamilyClient = PlatformEvents.FamilyClient;

    public static IReadOnlyList<WebhookEventDefinition> All { get; } =
    [
        // ── Leads ───────────────────────────────────────────────────────────────────
        Define<LeadCreatedEvent>("lead.created",
            e => new() { ["leadId"] = e.LeadId, ["status"] = e.Status, ["source"] = e.Source, ["leadSourceId"] = e.LeadSourceConfigId, ["ownerId"] = e.OwnerId, ["agencyId"] = e.AgencyId, ["clientId"] = e.CustomerId },
            t => new LeadCreatedEvent(t, Sample.Lead, "New", "Web", Sample.Source, null, Sample.Agency, null)),

        Define<LeadAssignedEvent>("lead.assigned",
            e => new() { ["leadId"] = e.LeadId, ["userId"] = e.UserId, ["previousUserId"] = e.PreviousUserId, ["method"] = e.Method },
            t => new LeadAssignedEvent(t, Sample.Lead, Sample.User, null, Guid.Empty, "Dispatch")),

        Define<LeadStatusChangedEvent>("lead.status_changed",
            e => new() { ["leadId"] = e.LeadId, ["previousStatus"] = e.PreviousStatus, ["status"] = e.Status },
            t => new LeadStatusChangedEvent(t, Sample.Lead, "New", "Open")),

        Define<LeadStageChangedEvent>("lead.stage_changed",
            e => new() { ["leadId"] = e.LeadId, ["previousStage"] = e.PreviousStage, ["stage"] = e.Stage },
            t => new LeadStageChangedEvent(t, Sample.Lead, "ContactAttempted", "ContactEstablished")),

        Define<LeadQualifiedEvent>("lead.qualified",
            e => new() { ["leadId"] = e.LeadId, ["score"] = e.Score },
            t => new LeadQualifiedEvent(t, Sample.Lead, 72)),

        Define<LeadConvertedToClientEvent>("lead.converted",
            e => new() { ["leadId"] = e.LeadId, ["clientId"] = e.ClientId },
            t => new LeadConvertedToClientEvent(t, Sample.Lead, Sample.Client)),

        Define<LeadClosedEvent>("lead.closed",
            e => new() { ["leadId"] = e.LeadId, ["status"] = e.Status },
            t => new LeadClosedEvent(t, Sample.Lead, "Lost")),

        Define<LeadReopenedEvent>("lead.reopened",
            e => new() { ["leadId"] = e.LeadId, ["previousStatus"] = e.PreviousStatus, ["status"] = e.Status },
            t => new LeadReopenedEvent(t, Sample.Lead, "Lost", "Open")),

        Define<LeadMergedEvent>("lead.merged",
            e => new() { ["leadId"] = e.AbsorbedLeadId, ["survivorLeadId"] = e.SurvivorLeadId },
            t => new LeadMergedEvent(t, Sample.Lead, Guid.Parse("7c0f1f64-30a6-4a7b-9a1e-2b9c4a5d6e70"))),

        Define<LeadConsentWithdrawnEvent>("lead.consent_withdrawn",
            e => new() { ["leadId"] = e.LeadId, ["consentType"] = e.ConsentType },
            t => new LeadConsentWithdrawnEvent(t, Sample.Lead, "Marketing")),

        Define<LeadErasedEvent>("lead.erased",
            e => new() { ["leadId"] = e.LeadId, ["erasedAt"] = e.ErasedAt },
            t => new LeadErasedEvent(t, Sample.Lead, Sample.At)),

        // ── Projets ─────────────────────────────────────────────────────────────────
        Define<ProjectCreatedEvent>("project.created",
            e => new() { ["projectId"] = e.ProjectId, ["leadId"] = e.LeadId, ["clientId"] = e.ClientId, ["stage"] = e.Stage, ["productLineId"] = e.ProductLineId, ["amount"] = e.Amount, ["currency"] = e.Currency, ["ownerId"] = e.OwnerId },
            t => new ProjectCreatedEvent(t, Sample.Project, Sample.Lead, null, "Qualification", Sample.ProductLine, 1_500_000m, "XOF", Sample.User)),

        Define<ProjectStageChangedEvent>("project.stage_changed",
            e => new() { ["projectId"] = e.ProjectId, ["leadId"] = e.LeadId, ["clientId"] = e.ClientId, ["previousStage"] = e.PreviousStage, ["stage"] = e.Stage, ["previousStepId"] = e.PreviousStepId, ["stepId"] = e.StepId },
            t => new ProjectStageChangedEvent(t, Sample.Project, Sample.Lead, null, "Qualification", "Proposal", null, Sample.Step)),

        Define<ProjectClosedEvent>("project.closed",
            e => new() { ["projectId"] = e.ProjectId, ["leadId"] = e.LeadId, ["clientId"] = e.ClientId, ["outcome"] = e.Outcome, ["amount"] = e.Amount, ["currency"] = e.Currency },
            t => new ProjectClosedEvent(t, Sample.Project, Sample.Lead, Sample.Client, "Won", 1_500_000m, "XOF")),

        // ── Clients ─────────────────────────────────────────────────────────────────
        Define<ClientCreatedEvent>("client.created",
            e => new() { ["clientId"] = e.ClientId, ["clientType"] = e.ClientType, ["agencyId"] = e.AgencyId, ["advisorUserId"] = e.AdvisorUserId, ["sourceLeadId"] = e.SourceLeadId },
            t => new ClientCreatedEvent(t, Sample.Client, "CLI-000000", "Individual", Sample.Agency, Sample.User, Sample.Lead, Sample.User)),

        Define<ClientUpdatedEvent>("client.updated",
            e => new() { ["clientId"] = e.ClientId, ["fields"] = e.ChangedFields.ToArray() },
            t => new ClientUpdatedEvent(t, Sample.Client, ["profession", "employer"], Sample.User)),

        Define<ClientSensitiveDataChangedEvent>("client.updated",
            e => new() { ["clientId"] = e.ClientId, ["fields"] = e.ChangedFields.ToArray() },
            t => new ClientSensitiveDataChangedEvent(t, Sample.Client, ["primaryPhone"], "Correction", Sample.User)),

        Define<ClientActivatedEvent>("client.activated",
            e => new() { ["clientId"] = e.ClientId },
            t => new ClientActivatedEvent(t, Sample.Client)),

        Define<ClientSuspendedEvent>("client.suspended",
            e => new() { ["clientId"] = e.ClientId },
            t => new ClientSuspendedEvent(t, Sample.Client, "—", Sample.User)),

        Define<ClientArchivedEvent>("client.archived",
            e => new() { ["clientId"] = e.ClientId, ["archivedAt"] = e.ArchivedAt },
            t => new ClientArchivedEvent(t, Sample.Client, "—", Sample.User, Sample.At)),

        Define<ClientTransferredEvent>("client.transferred",
            e => new() { ["clientId"] = e.ClientId, ["fromAgencyId"] = e.FromAgencyId, ["toAgencyId"] = e.ToAgencyId, ["advisorUserId"] = e.AdvisorUserId },
            t => new ClientTransferredEvent(t, Sample.Client, Sample.Agency, Guid.Parse("4b1d0c6e-5f7a-4e2b-8c3d-9e0f1a2b3c4d"), Sample.User, Sample.User)),

        Define<ClientsMergedEvent>("client.merged",
            e => new() { ["clientId"] = e.AbsorbedClientId, ["survivorClientId"] = e.SurvivorClientId },
            t => new ClientsMergedEvent(t, Guid.Parse("5a6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d"), Sample.Client, Sample.User)),

        Define<ClientSegmentChangedEvent>("client.segment_changed",
            e => new() { ["clientId"] = e.ClientId, ["previousSegment"] = e.PreviousSegment, ["segment"] = e.NewSegment },
            t => new ClientSegmentChangedEvent(t, Sample.Client, "STANDARD", "PREMIUM")),

        Define<KycValidatedEvent>("client.kyc_validated",
            e => new() { ["clientId"] = e.CustomerEntityId, ["validatedAt"] = e.ValidatedAt },
            t => new KycValidatedEvent(t, Sample.Client, Sample.At)),

        Define<KycRejectedEvent>("client.kyc_rejected",
            e => new() { ["clientId"] = e.CustomerEntityId, ["rejectedAt"] = e.RejectedAt },
            t => new KycRejectedEvent(t, Sample.Client, "—", Sample.At)),

        Define<ClientAnonymizedEvent>("client.anonymized",
            e => new() { ["clientId"] = e.ClientId, ["anonymizedAt"] = e.AnonymizedAt },
            t => new ClientAnonymizedEvent(t, Sample.Client, Sample.At)),
    ];

    private static readonly Dictionary<Type, WebhookEventDefinition> ByEventType = All.ToDictionary(d => d.EventType);

    /// <summary>The distinct codes a subscription may choose, in catalogue order.</summary>
    public static IReadOnlyList<string> Codes { get; } = All.Select(d => d.Code).Distinct().ToList();

    /// <summary>The entry an integration event is announced under, or null when it is not a webhook event.</summary>
    public static WebhookEventDefinition? For(IIntegrationEvent @event)
        => ByEventType.GetValueOrDefault(@event.GetType());

    public static bool IsKnownCode(string code) => Codes.Contains(code, StringComparer.Ordinal);

    /// <summary>The code's name, family and description come from <see cref="PlatformEvents"/>: one wording for webhooks and workflows.</summary>
    private static WebhookEventDefinition Define<TEvent>(
        string code,
        Func<TEvent, Dictionary<string, object?>> data,
        Func<Guid, TEvent> sample,
        int version = 1,
        string? requiredModule = null)
        where TEvent : IIntegrationEvent
    {
        var platform = PlatformEvents.Get(code);
        return new(code, version, platform.Family, platform.Label, platform.Description, requiredModule, typeof(TEvent),
            e => data((TEvent)e),
            t => sample(t));
    }

    /// <summary>Fixed identifiers for the documented examples, so the documentation does not change on every read.</summary>
    private static class Sample
    {
        public static readonly Guid Lead = Guid.Parse("3f2b8c1e-6a4d-4b7e-9c2f-1d5e8a7b6c40");
        public static readonly Guid Client = Guid.Parse("8e9d7c6b-5a4f-4e3d-b2c1-0a9f8e7d6c5b");
        public static readonly Guid Project = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
        public static readonly Guid Agency = Guid.Parse("9f8e7d6c-5b4a-4f3e-8d2c-1b0a9f8e7d6c");
        public static readonly Guid User = Guid.Parse("2c3d4e5f-6a7b-4c8d-9e0f-1a2b3c4d5e6f");
        public static readonly Guid Source = Guid.Parse("6d5c4b3a-2f1e-4d0c-9b8a-7f6e5d4c3b2a");
        public static readonly Guid ProductLine = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");
        public static readonly Guid Step = Guid.Parse("5e4d3c2b-1a0f-4e9d-8c7b-6a5f4e3d2c1b");
        public static readonly DateTimeOffset At = new(2026, 10, 29, 9, 30, 0, TimeSpan.Zero);
    }
}

/// <param name="Code">Stable, lower-case, dotted: <c>family.event</c>. Never renamed once published.</param>
/// <param name="Version">Bumped when an existing key changes meaning; adding a key does not bump it.</param>
/// <param name="RequiredModule">A <c>TenantModules</c> code the tenant must have, or null for the socle.</param>
/// <param name="Data">The <c>data</c> object of the payload — the only fields that leave the platform.</param>
/// <param name="Sample">An example event for the documentation and the « Envoyer un test » button.</param>
internal sealed record WebhookEventDefinition(
    string Code,
    int Version,
    string Family,
    string Label,
    string Description,
    string? RequiredModule,
    Type EventType,
    Func<IIntegrationEvent, Dictionary<string, object?>> Data,
    Func<Guid, IIntegrationEvent> Sample);
