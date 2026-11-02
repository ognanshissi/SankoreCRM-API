using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.PublicApi;

/// <summary>
/// What a « Fin » step may do to the record when the circuit ends there (product owner,
/// 2026-11-02) — besides deciding the circuit's own outcome. One list for the activation check,
/// the module that runs them and the builder's labels. A code is tied to the entity of the
/// template: a project circuit cannot archive a lead.
/// </summary>
public static class WorkflowEndActions
{
    public const string ProjectWin = "project.win";
    public const string ProjectLose = "project.lose";
    public const string LeadQualify = "lead.qualify";
    public const string LeadLose = "lead.lose";
    public const string LeadArchive = "lead.archive";

    public sealed record Definition(string Code, string Label, string EntityType);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(ProjectWin, "Gagner le projet", "Opportunity"),
        new(ProjectLose, "Perdre le projet", "Opportunity"),
        new(LeadQualify, "Qualifier le lead", "Lead"),
        new(LeadLose, "Marquer le lead perdu", "Lead"),
        new(LeadArchive, "Archiver le lead", "Lead"),
    ];

    public static Definition? Find(string? code) =>
        All.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.Ordinal));

    public static IEnumerable<Definition> For(string entityType) =>
        All.Where(a => string.Equals(a.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A circuit reached a « Fin » step that carries an action: the module that owns the record runs it.
/// Written to the Workflow outbox in the transaction that ended the instance. The owner answers
/// with <see cref="WorkflowEndActionOutcome"/>, written into the instance's history.
/// </summary>
/// <param name="Approved">The outcome the « Fin » gave the circuit: validated, or refused.</param>
/// <param name="TemplateName">The circuit's name, for the motive of a lost project or lead.</param>
public sealed record WorkflowEndActionRequested(
    Guid TenantId,
    Guid InstanceId,
    Guid InstanceStepId,
    string EntityType,
    Guid EntityId,
    string Action,
    bool Approved,
    string? TemplateName) : IntegrationEventBase;

/// <summary>What became of a <see cref="WorkflowEndActionRequested"/> — one French sentence, for the history.</summary>
public sealed record WorkflowEndActionOutcome(
    Guid TenantId,
    Guid InstanceId,
    Guid InstanceStepId,
    bool Done,
    string Message) : IntegrationEventBase;
