using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.PublicApi;

/// <summary>
/// Public contract exposed by the Workflow module. Other modules reference only
/// this interface — never the main assembly or any of its internals.
/// </summary>
public interface IWorkflowModule
{
    /// <summary>
    /// Starts a workflow instance for the given entity, using the active template
    /// registered for <paramref name="request"/>.EntityType in the caller's tenant.
    /// Returns the new instance Id on success, or a failure result when no template
    /// is configured for that entity type.
    /// </summary>
    Task<Result<Guid>> StartWorkflowAsync(WorkflowStartRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the current status of a workflow instance.
    /// Returns a failure result when the instance does not exist or belongs to a
    /// different tenant than <paramref name="tenantId"/>.
    /// </summary>
    Task<Result<WorkflowInstanceStatus>> GetInstanceStatusAsync(
        Guid instanceId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// The ACTIVE circuits of <paramref name="entityType"/> — for a project step to name the one it
    /// starts (« Opportunity », the project, can have several: a credit committee, a group-insurance committee…).
    /// </summary>
    Task<IReadOnlyList<WorkflowCircuitSummary>> ListActiveCircuitsAsync(
        Guid tenantId, string entityType, CancellationToken ct = default);

    /// <summary>
    /// The decisions taken on each of <paramref name="instanceIds"/>, step by step — for the module
    /// that started them to show who validated, refused or still has to (the project page shows
    /// the validations of each step's circuit). Unknown ids, or ids of another tenant, are skipped.
    /// </summary>
    Task<IReadOnlyList<WorkflowInstanceDecisions>> GetInstanceDecisionsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> instanceIds, CancellationToken ct = default);

    /// <summary>
    /// Mirrors a decision a CALLING module has already taken onto its workflow instance.
    ///
    /// <para>
    /// This is not "ask the engine to decide". The caller owns the decision — it has already applied
    /// it to its own aggregate — and this records it so the instance, its audit trail and the
    /// analytics stay in step. That is why it takes no approver role and performs no entitlement
    /// check: those belong to whoever owned the decision, and re-deciding here would make the engine
    /// a second authority over a rule it cannot see.
    /// </para>
    ///
    /// <para>
    /// Fails rather than throws when there is nothing to record — an unknown instance, an instance
    /// already in a terminal state, or a step order that is not the one awaiting a decision. Callers
    /// are expected to treat a failure as "the mirror is out of step", log it, and carry on: losing
    /// a traceability record must never roll back the decision it was mirroring.
    /// </para>
    /// </summary>
    Task<Result> RecordDecisionAsync(
        RecordWorkflowDecisionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Respect des délais des étapes chronométrées qu'un utilisateur a traitées, par nom d'étape.
    ///
    /// <para>
    /// Compte les étapes dotées d'un SLA et closes dans <c>[from, to)</c> : décidées par
    /// l'utilisateur (approuvées ou rejetées), ou expirées alors qu'elles lui étaient assignées.
    /// Une étape décidée avant son échéance est dans les délais ; une étape expirée ne l'est jamais.
    /// Les étapes sans SLA, sautées ou approuvées automatiquement ne disent rien de l'utilisateur
    /// et ne comptent pas. Regroupées par nom parce que c'est ce que l'écran affiche : deux
    /// parcours qui nomment pareil une étape la mesurent ensemble.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<StepSlaStat>> GetStepSlaStatsAsync(
        Guid tenantId, Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>
    /// Les étapes « bloquantes » pas encore franchies des parcours en cours sur un dossier
    /// (<paramref name="entityType"/> <c>Lead</c>, <c>Opportunity</c>…), au format « Étape (parcours) ».
    /// Choix du propriétaire, 2026-10-29 : tant qu'il en reste une, le lead n'est pas converti en
    /// client et le projet n'est pas clôturé gagné. Vide quand rien ne bloque.
    /// </summary>
    Task<IReadOnlyList<string>> GetBlockingStepsAsync(
        Guid tenantId, string entityType, Guid entityId, CancellationToken ct = default);
}

/// <param name="StepName">Le nom de l'étape tel que le parcours le définit.</param>
/// <param name="Total">Étapes closes sur la période.</param>
/// <param name="OnTime">Dont décidées avant leur échéance.</param>
public sealed record StepSlaStat(string StepName, int Total, int OnTime);

/// <param name="RequiredStepOrders">
/// The orders of the template steps that actually apply to this entity; every other step is marked
/// skipped as the instance starts. Null means "all of them".
///
/// <para>
/// It exists because a template is a fixed list of steps while a caller's approval ladder can be
/// computed per entity — M02's KYC circuit is one or three rungs depending on the file's vigilance
/// level, its duplicate flag and its face-match attempts. The engine could evaluate that itself
/// through <c>SkipIf</c>/<c>RequireIf</c> rules, and deliberately does not: the ladder is a
/// compliance rule owned by the module that enforces it, and expressing it here as well would give
/// it two homes and let them drift.
/// </para>
/// </param>
/// <param name="Circuit">
/// The circuit to start among the entity type's active ones; null takes the first (the historic,
/// single-circuit behaviour of every entity type but projects).
/// </param>
public sealed record WorkflowStartRequest(
    string EntityType,
    Guid EntityId,
    Guid StartedByUserId,
    Guid TenantId,
    IReadOnlyCollection<int>? RequiredStepOrders = null,
    string? Circuit = null,
    WorkflowSubject? Subject = null);

/// <summary>
/// What the instance validates, in the starting module's words — what an approver sees in their
/// queue instead of a bare « Validation ». A snapshot of the submission.
/// </summary>
/// <param name="Link">An app path that opens the subject (« /leads/projets/{id} »).</param>
public sealed record WorkflowSubject(string Label, string? Detail = null, string? Link = null);

/// <param name="Status">Pending, InProgress, Completed, Rejected, Cancelled, TimedOut…</param>
public sealed record WorkflowInstanceDecisions(
    Guid InstanceId,
    string WorkflowName,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkflowStepDecision> Steps);

/// <param name="Status">Pending, AwaitingApproval, Approved, Rejected, Skipped, TimedOut…</param>
/// <param name="ActedByUserId">Who approved or refused; null while pending or when the engine decided.</param>
public sealed record WorkflowStepDecision(
    int Order,
    string Name,
    string? ApproverRoleCode,
    string Status,
    Guid? ActedByUserId,
    string? Comment,
    DateTimeOffset? DueAt,
    DateTimeOffset? DecidedAt);

/// <param name="Circuit">The stable key a project step stores; survives new versions.</param>
/// <param name="TemplateId">The active version's template.</param>
public sealed record WorkflowCircuitSummary(string Circuit, string Name, int Version, int StepCount, Guid TemplateId = default);

/// <summary>What the calling module decided, in the engine's vocabulary.</summary>
public enum WorkflowDecision
{
    /// <summary>Advances the instance to its next applicable step, or completes it on the last one.</summary>
    Approved,

    /// <summary>Stops the instance — a refusal is terminal.</summary>
    Rejected,

    /// <summary>
    /// Abandons this instance without a verdict. For a decision that sends the entity back to its
    /// author — M02's "complement required" — so a later pass gets a fresh instance and the
    /// analytics count attempts truthfully instead of showing one long run.
    /// </summary>
    Cancelled
}

/// <param name="StepOrder">
/// Which step the decision was taken on. Matched by ORDER rather than by code: a step's
/// <c>Code</c> is derived from its order anyway, and an order is what a caller's own ladder already
/// carries (M02 stores it as <c>KycApprovalStep.LevelRank</c>). Ignored for
/// <see cref="WorkflowDecision.Cancelled"/>, which applies to the instance rather than a step.
/// </param>
/// <param name="Comment">
/// The decider's motive, copied into the instance's audit entry. An operator's words, never a field
/// value.
/// </param>
public sealed record RecordWorkflowDecisionRequest(
    Guid InstanceId,
    Guid TenantId,
    int StepOrder,
    WorkflowDecision Decision,
    Guid ActedByUserId,
    string? Comment = null);

public sealed record WorkflowInstanceStatus(
    Guid InstanceId,
    string Status,
    int CurrentStepOrder,
    int TotalSteps,
    bool IsCompleted);
