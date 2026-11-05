namespace Sankore.Shared.Infrastructure.Workflow;

/// <summary>
/// What a workflow step that WAITS ON THE RECORD needs to know about it, answered by the module
/// that owns the record — one implementation per workflow entity type, like
/// <see cref="IContextProvider"/>:
/// <list type="bullet">
/// <item>« Formulaire »: which fields can be required, and which of them are still empty;</item>
/// <item>« Formulaire de qualification »: how many questionnaires are still to complete;</item>
/// <item>« Vérification KYC »: which client's KYC the record depends on.</item>
/// </list>
/// Presence only — never a value: the Workflow module must not hold a record's data.
/// </summary>
public interface IWorkflowRecordGate
{
    /// <summary>The workflow entity type (<c>Lead</c>, <c>Opportunity</c>, <c>Account</c>).</summary>
    string EntityType { get; }

    /// <summary>The fields a « Formulaire » step may require — the tenant's form, native and custom.</summary>
    Task<IReadOnlyList<RecordFormField>> FieldsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The labels of <paramref name="keys"/> that are empty on the record. Unknown keys are ignored.</summary>
    Task<IReadOnlyList<string>> MissingFieldsAsync(Guid tenantId, Guid entityId, IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>Questionnaires still « à compléter » for the record; null when it has none to fill.</summary>
    Task<int?> PendingQuestionnairesAsync(Guid tenantId, Guid entityId, CancellationToken ct);

    /// <summary>The client whose KYC the record depends on; null while there is none (a lead not converted yet).</summary>
    Task<Guid?> ClientIdAsync(Guid tenantId, Guid entityId, CancellationToken ct);

    /// <summary>
    /// The record whose fields a « Formulaire » step reads — where a person completes them. The record
    /// itself by default; a project's form is its lead's.
    /// </summary>
    Task<RecordRef?> FieldsRecordAsync(Guid tenantId, Guid entityId, CancellationToken ct)
        => Task.FromResult<RecordRef?>(new RecordRef(EntityType, entityId));

    /// <summary>
    /// The records among <paramref name="entityIds"/> whose steps need separate signers inside their
    /// agency's perimeter — a credit file. Absent means the engine's usual rule, the step's role: an
    /// agent signing a step on their own lead is a choice of the product owner (2026-10-30).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, RecordSigningRule>> SigningRulesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> entityIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<Guid, RecordSigningRule>>(new Dictionary<Guid, RecordSigningRule>());
}

/// <summary>Who may not sign a record's steps, and where it belongs.</summary>
/// <param name="PreparedBy">Whoever works the file: they do not sign their own.</param>
/// <param name="AgencyId">The record's own agency, used when it has no client yet.</param>
/// <param name="ClientId">The record's client, whose agency decides who can read the file.</param>
public sealed record RecordSigningRule(Guid? PreparedBy, Guid? AgencyId, Guid? ClientId);

public sealed record RecordFormField(string Key, string Label);

/// <summary>A record of the CRM, as a workflow names it (<c>Lead</c>, <c>Opportunity</c>, <c>Account</c>).</summary>
public sealed record RecordRef(string EntityType, Guid Id);
