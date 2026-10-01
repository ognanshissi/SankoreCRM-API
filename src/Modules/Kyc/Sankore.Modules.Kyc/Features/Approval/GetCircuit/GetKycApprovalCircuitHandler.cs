namespace Sankore.Modules.Kyc.Features.Approval.GetCircuit;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetKycApprovalCircuitHandler(KycDbContext db, KycApprovalCircuit circuit)
    : IRequestHandler<GetKycApprovalCircuitQuery, Result<KycApprovalCircuitDto>>
{
    public async Task<Result<KycApprovalCircuitDto>> Handle(
        GetKycApprovalCircuitQuery query, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant; a file of another tenant is
        // simply absent and the caller is told NOT_FOUND, never FORBIDDEN.
        var file = await db.KycFiles.FirstOrDefaultAsync(f => f.Id == query.KycFileId, ct);

        if (file is null)
            return Result.Fail<KycApprovalCircuitDto>(KycErrors.FileNotFound);

        var steps = await db.KycApprovalSteps
            .Where(s => s.KycFileId == query.KycFileId)
            .ToListAsync(ct);

        // Sorted in memory: the level is stored as text and the database would order it
        // alphabetically, which agrees with the ladder only by accident.
        steps.Sort((a, b) => a.Level.CompareTo(b.Level));

        if (steps.Count > 0)
        {
            var next = steps.FirstOrDefault(s => s.Decision == KycApprovalDecision.Pending);

            return Result.Ok(new KycApprovalCircuitDto(
                file.Id,
                file.Status.ToString(),
                file.VigilanceLevel.ToString(),
                file.DuplicateSuspected,
                file.FaceMatchAttempts,
                IsStarted: true,
                NextLevel: next?.Level.ToString(),
                Steps: [.. steps.Select(s => new KycApprovalStepDto(
                    s.Level.ToString(),
                    s.Decision.ToString(),
                    s.ApproverId,
                    s.Comment,
                    s.DecidedAt))]));
        }

        // Nothing persisted yet. The panel still shows the ladder the file is heading for, rather
        // than an empty box an agent reads as "nobody has to approve this" — it is answered from
        // the same service that will create the rows, so the preview cannot drift from the circuit.
        var planned = await circuit.ResolveAsync(file, ct);

        return Result.Ok(new KycApprovalCircuitDto(
            file.Id,
            file.Status.ToString(),
            file.VigilanceLevel.ToString(),
            file.DuplicateSuspected,
            file.FaceMatchAttempts,
            IsStarted: false,
            NextLevel: null,
            Steps: [.. planned.Select(l => new KycApprovalStepDto(
                l.ToString(),
                KycApprovalDecision.Pending.ToString(),
                ApproverId: null,
                Comment: null,
                DecidedAt: null))]));
    }
}
