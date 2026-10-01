namespace Sankore.Modules.Kyc.Features.Approval.StartApproval;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// KYC-B-05 — writes the circuit a file must climb, once.
///
/// <para>
/// Idempotency is the whole difficulty here. The command is sent from a verification outcome, and
/// a verification can be replayed: a redelivered event, a Hangfire retry, two agents launching the
/// same file. A second set of <c>Pending</c> steps would mean the same rung signed twice, so the
/// read below covers the ordinary repeat and the unique index
/// <c>ux_kyc_approval_steps_file_level</c> covers the simultaneous one — the same two-layer shape
/// <c>CreateKycFileHandler</c> uses, and for the same reason: the read can be overtaken between
/// its query and its commit.
/// </para>
/// </summary>
internal sealed class StartKycApprovalHandler(
    KycDbContext db,
    KycApprovalCircuit circuit,
    IWorkflowModule workflow,
    TimeProvider clock,
    ILogger<StartKycApprovalHandler> logger)
    : IRequestHandler<StartKycApprovalCommand, Result<StartKycApprovalResult>>
{
    /// <summary>
    /// What M12 registers its template against. A constant because a typo would open a workflow
    /// nobody is looking for, silently.
    /// </summary>
    internal const string WorkflowEntityType = "KycFile";

    public async Task<Result<StartKycApprovalResult>> Handle(
        StartKycApprovalCommand cmd, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit tenant: the sender is a consumer or a job, so the ambient
        // tenant is not the one being processed.
        var file = await db.KycFiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId && f.TenantId == cmd.TenantId, ct);

        if (file is null)
            return Result.Fail<StartKycApprovalResult>(KycErrors.FileNotFound);

        var existing = await LoadLevelsAsync(cmd, ct);
        if (existing.Count > 0)
            return Result.Ok(new StartKycApprovalResult(
                file.Id, Describe(existing), AlreadyStarted: true, WorkflowInstanceId: null));

        var levels = await circuit.ResolveAsync(file, ct);

        foreach (var level in levels)
        {
            db.KycApprovalSteps.Add(
                KycApprovalStep.Pending(cmd.TenantId, file.Id, level, clock));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsCircuitUniqueViolation(ex))
        {
            // Lost the race. The caller's intent — "this file must have a circuit" — is satisfied
            // by the winner, and starting a second workflow for it would put two approval tasks
            // in front of the same branch manager.
            logger.LogInformation(
                "Concurrent approval circuit creation for KYC file {KycFileId}; keeping the existing one",
                file.Id);

            var winner = await LoadLevelsAsync(cmd, ct);

            return winner.Count > 0
                ? Result.Ok(new StartKycApprovalResult(
                    file.Id, Describe(winner), AlreadyStarted: true, WorkflowInstanceId: null))
                : Result.Fail<StartKycApprovalResult>(KycErrors.InvalidTransition);
        }

        // Started AFTER the steps are committed, and only when this call is the one that created
        // them: the workflow is a record of a circuit that exists, not the thing that creates it.
        var workflowInstanceId = await StartWorkflowAsync(cmd, ct);

        logger.LogInformation(
            "Approval circuit opened on KYC file {KycFileId}: {Levels}",
            file.Id, string.Join(" → ", levels));

        return Result.Ok(new StartKycApprovalResult(
            file.Id, Describe(levels), AlreadyStarted: false, workflowInstanceId));
    }

    /// <summary>
    /// M12 gives the approvers a task list and an audit trail of the chain, but it is NOT what
    /// guarantees anything: the engine enforces no self-approval rule of its own — M01 learned
    /// that on client merges — so M02 keeps four eyes in <c>KycFile.Approve</c>. A missing
    /// template, or a workflow module that throws, therefore degrades to a log line: the steps are
    /// already written and the circuit works without it. Blocking here would leave a file in
    /// Validating with nobody able to sign it, over a traceability nicety.
    /// </summary>
    private async Task<Guid?> StartWorkflowAsync(StartKycApprovalCommand cmd, CancellationToken ct)
    {
        try
        {
            var started = await workflow.StartWorkflowAsync(
                new WorkflowStartRequest(WorkflowEntityType, cmd.KycFileId, cmd.StartedBy, cmd.TenantId),
                ct);

            if (started.IsSuccess) return started.Value;

            logger.LogWarning(
                "No workflow instance for the approval circuit of KYC file {KycFileId} "
                + "(tenant {TenantId}): {Error}. The circuit stands and four eyes are enforced by M02.",
                cmd.KycFileId, cmd.TenantId, started.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Starting the KycFile workflow failed for KYC file {KycFileId} (tenant {TenantId}). "
                + "The circuit stands and four eyes are enforced by M02.",
                cmd.KycFileId, cmd.TenantId);
        }

        return null;
    }

    private async Task<IReadOnlyList<KycApprovalLevel>> LoadLevelsAsync(
        StartKycApprovalCommand cmd, CancellationToken ct)
    {
        var levels = await db.KycApprovalSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == cmd.TenantId && s.KycFileId == cmd.KycFileId)
            .Select(s => s.Level)
            .ToListAsync(ct);

        // Sorted in memory, never with an ORDER BY on the column: the level is persisted as text,
        // so the database would sort it alphabetically. It happens to agree with the ladder today
        // and would stop agreeing the first time a level is renamed — M13 paid for that lesson on
        // priority ordering. A circuit is at most three rows; the sort costs nothing.
        levels.Sort();
        return levels;
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<KycApprovalLevel> levels)
        => [.. levels.Select(l => l.ToString())];

    /// <summary>
    /// Narrow on purpose: only the circuit's own index may be swallowed. Any other constraint
    /// violation is a bug and must keep propagating instead of being reported as "already there".
    /// </summary>
    private static bool IsCircuitUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("ux_kyc_approval_steps_file_level",
               StringComparison.OrdinalIgnoreCase) == true;
}
