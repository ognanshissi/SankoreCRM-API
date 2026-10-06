namespace Sankore.Modules.Kyc.Features.Approval;

using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Settings;

/// <summary>
/// KYC-B-05 — decides WHICH rungs a file must climb before it is validated, and nothing else.
///
/// <para>
/// The graduated approach of the BCEAO is a rule about who signs, not about how the signing is
/// recorded, so it lives in one service rather than being spelled out in the handler that creates
/// the steps and again in the screen that displays them. <c>StartKycApprovalHandler</c> asks it
/// once, at the moment the file enters validation, and the rows it writes are what the decision
/// handler then reads — a circuit must not change under an approver's feet because a flag was
/// lifted while the file was waiting.
/// </para>
///
/// <para>
/// Levels come back in ladder order, which is the numeric order of
/// <see cref="KycApprovalLevel"/> — Agent, then BranchManager, then ComplianceOfficer. The set is
/// sorted rather than appended in sequence so no caller can produce the same level twice: the
/// unique index <c>ux_kyc_approval_steps_file_level</c> would refuse it, and a circuit that cannot
/// be created is a file that can never be validated.
/// </para>
/// </summary>
internal sealed class KycApprovalCircuit(IKycSettings settings)
{
    public async Task<IReadOnlyList<KycApprovalLevel>> ResolveAsync(
        KycFile file, CancellationToken ct)
    {
        var levels = new SortedSet<KycApprovalLevel>
        {
            // Every circuit starts with an agent's signature.
            KycApprovalLevel.Agent,
        };

        // A low-risk file is signed by the agent alone — an explicit arbitrage by the product
        // owner, taken knowing it departs from the original wording of KYC-B-05 ("risque faible ou
        // standard : agent puis chef d'agence"). Four eyes still hold: DecideKycApprovalHandler
        // refuses the submitter on every rung, so the signing agent is never the one who built the
        // file. What is given up is the second PAIR of eyes on a low-risk file, not the rule.
        //
        // It also makes the face-attempt clause below bite for the first time: before this, the
        // branch manager was on every circuit anyway.
        if (file.VigilanceLevel != KycVigilanceLevel.Low)
            levels.Add(KycApprovalLevel.BranchManager);

        // Vigilance élevée, or a document number that collided with another open file: compliance
        // has the last word. DuplicateSuspected is checked on its own and not folded into the
        // vigilance test, because the flag can be lifted (KYC-B-04) while the level it raised
        // stays — so a file can be High without being a suspected duplicate, and the reverse must
        // keep working if the two ever drift apart.
        if (file.VigilanceLevel == KycVigilanceLevel.High || file.DuplicateSuspected)
            levels.Add(KycApprovalLevel.ComplianceOfficer);

        // Independent of the risk rating, deliberately. Two failed face comparisons say something
        // about the CAPTURE, not about the customer: somebody other than the agent who took the
        // photographs has to look at them before the file is validated. Since low-risk files are
        // now signed by the agent alone, this is the clause that puts the branch manager back on
        // one — which is exactly what KYC-B-05 asks for, "quel que soit le risque".
        var maxAttempts = await settings.GetIntAsync(
            file.TenantId, KycSettingKeys.FaceMatchMaxAttempts, ct);

        if (maxAttempts > 0 && file.FaceMatchAttempts >= maxAttempts)
            levels.Add(KycApprovalLevel.BranchManager);

        // Same reasoning, same remedy, different cause: a file validated by hand has no biometric
        // score behind it at all, so somebody other than the validator has to look before it is
        // approved.
        //
        // Without this clause the four-eyes anchor on ManuallyValidatedBy would make a LOW-risk
        // file unsignable — its ladder is the agent alone, and the one person forbidden from
        // signing it is the one who just validated it. Widening the ladder here rather than by
        // raising the file's vigilance level keeps the change to WHO SIGNS: vigilance also selects
        // the review periodicity, and a file manually validated today should not quietly acquire a
        // shorter re-review cycle as a side effect.
        if (file.ManuallyValidatedBy is not null)
            levels.Add(KycApprovalLevel.BranchManager);

        return [.. levels];
    }
}
