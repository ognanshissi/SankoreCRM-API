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
}

public sealed record RecordFormField(string Key, string Label);
