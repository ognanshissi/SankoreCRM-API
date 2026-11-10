namespace Sankore.Modules.Kyc.Features.Files.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// The institution switched its KYC off (docs/kyc-par-institution.md): the validation circuits
/// mirroring its files leave the approvers' queues — cancelled and unlinked, as a complement does.
///
/// <para>
/// The files themselves are left as they are: readable, frozen by the route filter. Switched back
/// on, a file still in validation is decided in M02 as before; its next submission opens a fresh
/// mirror. Production's files and its « Mode test » twin's, which follows the same switch.
/// </para>
/// </summary>
public sealed class TenantKycDisabledKycConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<TenantKycDisabledKycConsumer> logger)
    : IConsumer<TenantKycSwitchedEvent>
{
    internal const string Motive = "KYC désactivé pour l'institution : le dossier reste consultable.";

    public async Task Consume(ConsumeContext<TenantKycSwitchedEvent> context)
    {
        var evt = context.Message;
        if (evt.Enabled || evt.TenantId == Guid.Empty) return;

        Guid? twin;
        using (var probe = scopeFactory.CreateScope())
        {
            if (await probe.ServiceProvider.GetRequiredService<ITenantKyc>().IsInForceAsync(evt.TenantId, context.CancellationToken))
                return;
            twin = (await probe.ServiceProvider.GetRequiredService<ITenantStore>().GetAsync(evt.TenantId, context.CancellationToken))?.TestTenantId;
        }

        foreach (var tenantId in twin is { } t ? [evt.TenantId, t] : new[] { evt.TenantId })
            await WithdrawCircuitsAsync(tenantId, evt.ActorId, context.CancellationToken);
    }

    private async Task WithdrawCircuitsAsync(Guid tenantId, Guid actor, CancellationToken ct)
    {
        using var bg = BackgroundJobContext.SetScope(tenantId, actor, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var workflow = scope.ServiceProvider.GetRequiredService<IWorkflowModule>();

        var files = await db.KycFiles
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(f => f.TenantId == tenantId && f.WorkflowInstanceId != null)
            .ToListAsync(ct);

        foreach (var file in files)
        {
            var pending = await db.KycApprovalSteps
                .Where(s => s.KycFileId == file.Id && s.Decision == KycApprovalDecision.Pending)
                .Select(s => s.Level)
                .ToListAsync(ct);
            if (pending.Count == 0) continue;

            try
            {
                var recorded = await workflow.RecordDecisionAsync(
                    new RecordWorkflowDecisionRequest(
                        InstanceId: file.WorkflowInstanceId!.Value,
                        TenantId: tenantId,
                        StepOrder: (int)pending.Min(),
                        Decision: WorkflowDecision.Cancelled,
                        ActedByUserId: actor,
                        Comment: Motive),
                    ct);

                if (recorded.IsFailure)
                {
                    logger.LogWarning("KYC file {KycFileId}: circuit not withdrawn after KYC was switched off: {Error}",
                        file.Id, recorded.Error);
                    continue;
                }

                file.ClearWorkflowInstance();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // M02 is the record: a mirror left behind is a stale queue line, never a decision.
                logger.LogWarning(ex, "KYC file {KycFileId}: withdrawing its circuit failed", file.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
