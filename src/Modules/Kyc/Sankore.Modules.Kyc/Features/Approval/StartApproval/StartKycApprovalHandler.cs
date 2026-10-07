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
        // AsTracking: the mirror's instance id is written onto the file below, and the context is
        // NoTracking by default — without it LinkWorkflowInstance would mutate a detached instance
        // and SaveChanges would write nothing.
        var file = await db.KycFiles
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId && f.TenantId == cmd.TenantId, ct);

        if (file is null)
            return Result.Fail<StartKycApprovalResult>(KycErrors.FileNotFound);

        var existing = await LoadLevelsAsync(cmd, ct);
        if (existing.Count > 0)
        {
            // The circuit is already there, but this round may still need a mirror: a complement
            // request cancels the instance and clears the id, and the steps are REUSED on the way
            // back. That is why these paths used to return null and the link was lost.
            var mirrored = await EnsureWorkflowAsync(cmd, file, existing, ct);

            return Result.Ok(new StartKycApprovalResult(
                file.Id, Describe(existing), AlreadyStarted: true, mirrored));
        }

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

            if (winner.Count == 0)
                return Result.Fail<StartKycApprovalResult>(KycErrors.InvalidTransition);

            // The loser does not start a second workflow — that would put two approval tasks in
            // front of the same branch manager — but it does link the file to one if the winner
            // somehow left it without.
            var mirrored = await EnsureWorkflowAsync(cmd, file, winner, ct);

            return Result.Ok(new StartKycApprovalResult(
                file.Id, Describe(winner), AlreadyStarted: true, mirrored));
        }

        // Started AFTER the steps are committed, and only when this call is the one that created
        // them: the workflow is a record of a circuit that exists, not the thing that creates it.
        var workflowInstanceId = await EnsureWorkflowAsync(cmd, file, levels, ct);

        logger.LogInformation(
            "Approval circuit opened on KYC file {KycFileId}: {Levels}",
            file.Id, string.Join(" → ", levels));

        return Result.Ok(new StartKycApprovalResult(
            file.Id, Describe(levels), AlreadyStarted: false, workflowInstanceId));
    }

    /// <summary>
    /// Makes sure this round of validation has a workflow instance behind it, and remembers which.
    ///
    /// <para>
    /// M12 gives the approvers a cross-module task list, an audit trail of the chain and the cycle
    /// times, but it is NOT what guarantees anything: the engine enforces no self-approval rule of
    /// its own — M01 learned that on client merges — so M02 keeps four eyes in
    /// <c>KycFile.Approve</c> and on every rung of <c>DecideKycApprovalHandler</c>. A missing
    /// template, or a workflow module that throws, therefore degrades to a log line: the steps are
    /// already written and the circuit works without it. Blocking here would leave a file in
    /// Validating with nobody able to sign it, over a traceability nicety.
    /// </para>
    ///
    /// <para>
    /// Idempotent on the file: a file that already carries an instance id keeps it, so a replayed
    /// verification cannot open a second instance for one round.
    /// </para>
    /// </summary>
    /// <param name="levels">
    /// The rungs this file actually has. Passed to M12 so the instance skips the steps that do not
    /// apply — a low-risk file is the agent alone, and an instance showing a branch-manager step
    /// waiting would be inviting a decision nobody will be asked for. The ladder itself stays
    /// M02's: see <c>WorkflowStartRequest.RequiredStepOrders</c>.
    /// </param>
    private async Task<Guid?> EnsureWorkflowAsync(
        StartKycApprovalCommand cmd,
        KycFile file,
        IReadOnlyList<KycApprovalLevel> levels,
        CancellationToken ct)
    {
        if (file.WorkflowInstanceId is { } already) return already;

        try
        {
            var started = await workflow.StartWorkflowAsync(
                new WorkflowStartRequest(
                    WorkflowEntityType, cmd.KycFileId, cmd.StartedBy, cmd.TenantId,
                    RequiredStepOrders: [.. levels.Select(level => (int)level)]),
                ct);

            if (started.IsSuccess)
            {
                file.LinkWorkflowInstance(started.Value);
                await db.SaveChangesAsync(ct);
                return started.Value;
            }

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
