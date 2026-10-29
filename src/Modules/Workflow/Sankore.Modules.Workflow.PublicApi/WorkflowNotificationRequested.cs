using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.PublicApi;

/// <summary>
/// A « Notification » step of a running instance was reached: somebody must be told. Published
/// through the Workflow outbox in the transaction that moved the instance past the step — the step
/// is already franchie, the circuit never waits for the message.
///
/// <para>
/// <b>The Workflow module sends nothing itself.</b> It knows an entity type and an id; the person's
/// address, its encryption and its consent live in the module that owns the record, which is the one
/// that consumes this (Leads for a lead or a project, Customers for a client). A recipient it cannot
/// reach is a recorded reason, never an error that comes back here.
/// </para>
/// </summary>
/// <param name="InstanceStepId">The step reached. With « workflow.notification: » in front, the
/// idempotency key of every message this request produces: a redelivered request sends nothing twice.</param>
/// <param name="EntityType">The workflow entity — <c>Lead</c>, <c>Opportunity</c>, <c>Account</c>…</param>
/// <param name="Channel"><c>Email</c> or <c>Sms</c>, for the person of the record.</param>
/// <param name="Recipients"><see cref="WorkflowNotificationRecipients"/> values. Empty = nobody.</param>
/// <param name="RoleCodes">The roles told when <see cref="WorkflowNotificationRecipients.Role"/> is set.</param>
/// <param name="Subject">The e-mail subject (unused for a text message).</param>
/// <param name="Message">The text, variables not yet replaced — the owner module knows their values.</param>
public sealed record WorkflowNotificationRequested(
    Guid TenantId,
    Guid InstanceId,
    Guid InstanceStepId,
    string StepName,
    string EntityType,
    Guid EntityId,
    string Channel,
    IReadOnlyList<string> Recipients,
    IReadOnlyList<string> RoleCodes,
    string? Subject,
    string? Message) : IntegrationEventBase
{
    /// <summary>The e-mail and SMS template every module sends it with (data: <c>subject</c>, <c>body</c>).</summary>
    public const string TemplateKey = "workflow.notification";

    public string IdempotencyKey => $"workflow.notification:{InstanceStepId}";

    public bool Tells(string recipient) => Recipients.Contains(recipient, StringComparer.OrdinalIgnoreCase);

    public bool IsSms => string.Equals(Channel, "Sms", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Who a « Notification » step tells. Stable strings: stored in the step configuration.</summary>
public static class WorkflowNotificationRecipients
{
    /// <summary>The person the record is about: the lead, the client, a project's lead or client.</summary>
    public const string Subject = "subject";

    /// <summary>The agent the record is assigned to.</summary>
    public const string AssignedAgent = "assignedAgent";

    /// <summary>Every active user holding one of the step's roles.</summary>
    public const string Role = "role";
}

/// <summary>
/// What became of one audience of a « Notification » step — the person of the record, or the people
/// working it. Published by the module that sent (or could not send), recorded by Workflow in the
/// instance's history so the step says « mis en file » or « non envoyé : pas de consentement »
/// rather than leaving the reason in a log.
///
/// <para>
/// « Sent » means QUEUED: the provider may still refuse the address. Delivery is the notification
/// module's own log.
/// </para>
/// </summary>
/// <param name="Detail">One French sentence for the instance screen. No address, no number.</param>
public sealed record WorkflowNotificationOutcome(
    Guid TenantId,
    Guid InstanceId,
    Guid InstanceStepId,
    bool Sent,
    string Detail) : IntegrationEventBase
{
    public static WorkflowNotificationOutcome Queued(WorkflowNotificationRequested request, string detail)
        => new(request.TenantId, request.InstanceId, request.InstanceStepId, true, detail);

    public static WorkflowNotificationOutcome NotSent(WorkflowNotificationRequested request, string reason)
        => new(request.TenantId, request.InstanceId, request.InstanceStepId, false, $"Non envoyé : {reason}");
}
