namespace Sankore.Modules.Leads.Features.Questionnaires.RecordHealthConsent;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records one person's explicit consent to health-data processing (BNF-B-09).
/// </summary>
/// <param name="ProofReference">
/// Where the evidence lives — a signed form in object storage, a recording reference. Required: a
/// consent nobody can produce evidence for is not one that would survive a regulator asking.
/// </param>
/// <param name="GivenById">
/// Who gave it, when that is not the subject — a parent for a minor. Null when the subject
/// consented for themselves.
/// </param>
internal sealed record RecordHealthConsentCommand(
    Guid LeadId,
    string SubjectType,
    Guid SubjectId,
    string Channel,
    string ProofReference,
    Guid? GivenById = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
