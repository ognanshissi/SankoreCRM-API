using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure.Gates;

/// <summary>
/// What the steps that wait ON THE RECORD require of it, asked of the module that owns it
/// (<see cref="IWorkflowRecordGate"/>):
/// <list type="bullet">
/// <item>« Formulaire »: the fields chosen in the step (<c>{"fields": ["email", …]}</c>) are filled;</item>
/// <item>« Formulaire de qualification »: no questionnaire is left « à compléter »;</item>
/// <item>« Vérification KYC »: the record's client has a validated KYC at the step's level
/// (<c>{"kycLevel": "Full"|"Simplified"}</c>, Full by default).</item>
/// </list>
/// An approval is refused while that is not so (product owner, 2026-10-29: an incomplete form must
/// not be approved). The KYC check also passes by itself as soon as the KYC is there.
/// </summary>
internal sealed class RecordStepGates(
    IEnumerable<IWorkflowRecordGate> gates,
    IKycModule kyc,
    Sankore.Modules.Integration.PublicApi.IIntegrationModule? integration = null,
    Sankore.Modules.Customers.PublicApi.ICustomersModule? customers = null)
{
    public IWorkflowRecordGate? Gate(string entityType)
        => gates.FirstOrDefault(g => string.Equals(g.EntityType, entityType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why the step may not be approved yet, in French — or null.</summary>
    public async Task<string?> ApprovalRefusalAsync(
        Guid tenantId, string entityType, Guid entityId, StepKind kind, string? configJson, CancellationToken ct)
    {
        switch (kind)
        {
            case StepKind.Form:
            {
                var keys = Fields(configJson);
                if (keys.Count == 0 || Gate(entityType) is not { } gate) return null;
                var missing = await gate.MissingFieldsAsync(tenantId, entityId, keys, ct);
                return missing.Count == 0 ? null : $"Champs à remplir avant d'approuver : {string.Join(", ", missing)}.";
            }
            case StepKind.QualificationForm:
            {
                if (Gate(entityType) is not { } gate) return null;
                var pending = await gate.PendingQuestionnairesAsync(tenantId, entityId, ct);
                return pending is > 0
                    ? $"{pending} questionnaire{(pending > 1 ? "s" : "")} à compléter avant d'approuver."
                    : null;
            }
            case StepKind.KycCheck:
            {
                var verdict = await KycAsync(tenantId, entityType, entityId, configJson, ct);
                return verdict.Passed ? null : verdict.Reason;
            }
            case StepKind.IntegrationAction:
            {
                var verdict = await IntegrationAsync(tenantId, entityType, entityId, configJson, ct);
                return verdict.Passed ? null : verdict.Reason;
            }
            default:
                return null;
        }
    }

    public sealed record KycVerdict(bool Passed, string Reason);

    public async Task<KycVerdict> KycAsync(Guid tenantId, string entityType, Guid entityId, string? configJson, CancellationToken ct)
    {
        var full = !string.Equals(KycLevel(configJson), "Simplified", StringComparison.OrdinalIgnoreCase);
        var levelLabel = full ? "complet" : "simplifié";

        var clientId = await ClientIdAsync(tenantId, entityType, entityId, ct);
        if (clientId is not { } client)
            return new KycVerdict(false, $"Pas encore de fiche client : l'étape attend un KYC {levelLabel} validé.");

        var status = await kyc.GetStatusAsync(tenantId, client, ct);
        if (status == KycStatus.Rejected)
            return new KycVerdict(false, "KYC du client refusé : l'étape attend une décision manuelle.");
        if (status != KycStatus.Approved)
            return new KycVerdict(false, $"KYC du client pas encore validé : l'étape attend un KYC {levelLabel}.");

        if (full)
        {
            var limits = await kyc.GetLimitsAsync(tenantId, client, ct);
            if (limits?.Tier != "Full")
                return new KycVerdict(false, "KYC du client validé en simplifié : l'étape attend un KYC complet.");
        }
        return new KycVerdict(true, $"KYC du client validé ({levelLabel}).");
    }

    /// <summary>
    /// The « Action Integration » step waits for the back office to know the record — it sends
    /// nothing itself (product owner, 2026-10-29): the client is created at the core banking once its
    /// KYC is validated, a contract is sent to the insurer once created, by the Integration module.
    /// <c>{"target": "CoreBanking"|"Insurance"}</c>; Insurance waits for the PROJECT's contract.
    /// </summary>
    public async Task<KycVerdict> IntegrationAsync(Guid tenantId, string entityType, Guid entityId, string? configJson, CancellationToken ct)
    {
        if (integration is null)
            return new KycVerdict(false, "Synchronisation indisponible : l'étape attend une décision manuelle.");

        if (IsInsurance(configJson))
        {
            var holding = customers is null || !string.Equals(entityType, "Opportunity", StringComparison.OrdinalIgnoreCase)
                ? null
                : await customers.GetProjectHoldingIdAsync(tenantId, entityId, ct);
            if (holding is not { } holdingId)
                return new KycVerdict(false, "Pas encore de contrat pour ce projet : l'étape attend son émission par l'assureur.");

            var state = await integration.GetSyncStateAsync(tenantId, Sankore.Modules.Integration.PublicApi.IntegrationSyncTarget.Insurance, holdingId, ct);
            return state.Synced ? new KycVerdict(true, "Contrat émis par l'assureur.")
                : state.Rejection is { } refusal ? new KycVerdict(false, $"Refus de l'assureur : {refusal}. L'étape attend une décision manuelle.")
                : new KycVerdict(false, "En attente de l'émission du contrat par l'assureur.");
        }

        if (await ClientIdAsync(tenantId, entityType, entityId, ct) is not { } client)
            return new KycVerdict(false, "Pas encore de fiche client : l'étape attend sa création au core banking.");

        var cbs = await integration.GetSyncStateAsync(tenantId, Sankore.Modules.Integration.PublicApi.IntegrationSyncTarget.CoreBanking, client, ct);
        return cbs.Synced ? new KycVerdict(true, "Client présent dans le core banking.")
            : cbs.Rejection is { } rejected ? new KycVerdict(false, $"Refus du core banking : {rejected}. L'étape attend une décision manuelle.")
            : new KycVerdict(false, "En attente de la création du client au core banking.");
    }

    public static bool IsInsurance(string? configJson)
        => Root(configJson) is { } root && root.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
        && string.Equals(t.GetString(), "Insurance", StringComparison.OrdinalIgnoreCase);

    public async Task<Guid?> ClientIdAsync(Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
    {
        if (string.Equals(entityType, "KycFile", StringComparison.OrdinalIgnoreCase))
            return (await kyc.GetKycFileSummaryAsync(tenantId, entityId, ct))?.CustomerId;
        return Gate(entityType) is { } gate ? await gate.ClientIdAsync(tenantId, entityId, ct) : null;
    }

    public static IReadOnlyList<string> Fields(string? configJson)
    {
        if (Root(configJson) is not { } root
            || !root.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            return [];
        return fields.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static string? KycLevel(string? configJson)
        => Root(configJson) is { } root && root.TryGetProperty("kycLevel", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement? Root(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A « Vérification KYC » step opened. Workflow-internal, through its outbox.</summary>
public sealed record KycCheckStepReached(Guid TenantId, Guid InstanceId, Guid InstanceStepId) : IntegrationEventBase;

/// <summary>
/// Checks a « Vérification KYC » step against its client's KYC — when the step opens, and again
/// when that client's KYC is validated or refused (<c>WorkflowTriggerConsumer</c>). Passed: the
/// circuit goes on. Otherwise the history says what the step waits for.
/// </summary>
internal sealed class KycStepRunner(
    WorkflowDbContext db,
    RecordStepGates gates,
    InstanceRunner runner,
    ILogger<KycStepRunner> logger)
{
    public async Task EvaluateAsync(Guid tenantId, Guid instanceStepId, CancellationToken ct)
    {
        var target = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Id == instanceStepId
                     && s.Kind == StepKind.KycCheck && s.Status == StepStatus.AwaitingApproval)
            .Join(db.WorkflowInstances.IgnoreQueryFilters(), s => s.InstanceId, i => i.Id,
                (s, i) => new { s.InstanceId, s.ConfigJson, i.EntityType, i.EntityId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return;

        var verdict = await gates.KycAsync(tenantId, target.EntityType, target.EntityId, target.ConfigJson, ct);
        if (verdict.Passed)
        {
            await runner.PassKycCheckAsync(target.InstanceId, instanceStepId, verdict.Reason, ct);
            return;
        }

        logger.LogInformation("KYC step {StepId} waits: {Reason}", instanceStepId, verdict.Reason);
        await StepHistory.AddAsync(db, tenantId, instanceStepId, EventCodes.KycPending, verdict.Reason, logger, ct);
    }

    /// <summary>The open KYC steps whose record depends on <paramref name="clientId"/>.</summary>
    public async Task ReevaluateForClientAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var open = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Kind == StepKind.KycCheck && s.Status == StepStatus.AwaitingApproval)
            .Join(db.WorkflowInstances.IgnoreQueryFilters().Where(i => i.Status == WorkflowStatus.InProgress),
                s => s.InstanceId, i => i.Id, (s, i) => new { s.Id, i.EntityType, i.EntityId })
            .ToListAsync(ct);

        foreach (var step in open)
        {
            if (await gates.ClientIdAsync(tenantId, step.EntityType, step.EntityId, ct) == clientId)
                await EvaluateAsync(tenantId, step.Id, ct);
        }
    }
}

public sealed class KycCheckStepConsumer(IServiceScopeFactory scopeFactory) : IConsumer<KycCheckStepReached>
{
    public async Task Consume(ConsumeContext<KycCheckStepReached> context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KycStepRunner>()
            .EvaluateAsync(context.Message.TenantId, context.Message.InstanceStepId, context.CancellationToken);
    }
}

/// <summary>An « Action Integration » step opened. Workflow-internal, through its outbox.</summary>
public sealed record IntegrationStepReached(Guid TenantId, Guid InstanceId, Guid InstanceStepId) : IntegrationEventBase;

/// <summary>
/// Checks an « Action Integration » step against what the back office recorded — when the step
/// opens, and whenever the back office answers in that tenant (a client created, a policy issued, a
/// command succeeded or refused). Passed: the circuit goes on; otherwise the history says what it
/// waits for, or the refusal.
/// </summary>
internal sealed class IntegrationStepRunner(
    WorkflowDbContext db,
    RecordStepGates gates,
    InstanceRunner runner,
    ILogger<IntegrationStepRunner> logger)
{
    public async Task EvaluateAsync(Guid tenantId, Guid instanceStepId, CancellationToken ct)
    {
        var target = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Id == instanceStepId
                     && s.Kind == StepKind.IntegrationAction && s.Status == StepStatus.AwaitingApproval)
            .Join(db.WorkflowInstances.IgnoreQueryFilters(), s => s.InstanceId, i => i.Id,
                (s, i) => new { s.InstanceId, s.ConfigJson, i.EntityType, i.EntityId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return;

        var verdict = await gates.IntegrationAsync(tenantId, target.EntityType, target.EntityId, target.ConfigJson, ct);
        if (verdict.Passed)
        {
            await runner.PassIntegrationAsync(target.InstanceId, instanceStepId, verdict.Reason, ct);
            return;
        }

        logger.LogInformation("Integration step {StepId} waits: {Reason}", instanceStepId, verdict.Reason);
        await StepHistory.AddAsync(db, tenantId, instanceStepId, EventCodes.IntegrationPending, verdict.Reason, logger, ct);
    }

    /// <summary>Every open integration step of the tenant: there are few, and a back-office answer names a client OR a holding.</summary>
    public async Task ReevaluateTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var open = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Kind == StepKind.IntegrationAction && s.Status == StepStatus.AwaitingApproval)
            .Join(db.WorkflowInstances.IgnoreQueryFilters().Where(i => i.Status == WorkflowStatus.InProgress),
                s => s.InstanceId, i => i.Id, (s, i) => s.Id)
            .ToListAsync(ct);
        foreach (var stepId in open)
            await EvaluateAsync(tenantId, stepId, ct);
    }
}

public sealed class IntegrationStepConsumer(IServiceScopeFactory scopeFactory) :
    IConsumer<IntegrationStepReached>,
    IConsumer<Sankore.Modules.Integration.PublicApi.CbsCustomerCreatedEvent>,
    IConsumer<Sankore.Modules.Integration.PublicApi.PolicyIssuedEvent>,
    IConsumer<Sankore.Modules.Integration.PublicApi.IntegrationCommandSucceededEvent>,
    IConsumer<Sankore.Modules.Integration.PublicApi.IntegrationCommandRejectedEvent>
{
    public Task Consume(ConsumeContext<IntegrationStepReached> context)
        => RunAsync(r => r.EvaluateAsync(context.Message.TenantId, context.Message.InstanceStepId, context.CancellationToken));

    public Task Consume(ConsumeContext<Sankore.Modules.Integration.PublicApi.CbsCustomerCreatedEvent> context)
        => RunAsync(r => r.ReevaluateTenantAsync(context.Message.TenantId, context.CancellationToken));

    public Task Consume(ConsumeContext<Sankore.Modules.Integration.PublicApi.PolicyIssuedEvent> context)
        => RunAsync(r => r.ReevaluateTenantAsync(context.Message.TenantId, context.CancellationToken));

    public Task Consume(ConsumeContext<Sankore.Modules.Integration.PublicApi.IntegrationCommandSucceededEvent> context)
        => RunAsync(r => r.ReevaluateTenantAsync(context.Message.TenantId, context.CancellationToken));

    public Task Consume(ConsumeContext<Sankore.Modules.Integration.PublicApi.IntegrationCommandRejectedEvent> context)
        => RunAsync(r => r.ReevaluateTenantAsync(context.Message.TenantId, context.CancellationToken));

    private async Task RunAsync(Func<IntegrationStepRunner, Task> run)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await run(scope.ServiceProvider.GetRequiredService<IntegrationStepRunner>());
    }
}
