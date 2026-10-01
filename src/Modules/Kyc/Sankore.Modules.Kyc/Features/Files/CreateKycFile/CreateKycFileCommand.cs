namespace Sankore.Modules.Kyc.Features.Files.CreateKycFile;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <param name="CustomerId">Opaque reference to the customer record owned by M01.</param>
/// <param name="InitiatedBy">
/// Who caused the file to exist. For the automatic triggers this is the actor carried by the
/// originating event — the agent who created the client, or the one who converted the lead — not
/// a SYSTEM placeholder: a file nobody is accountable for is a file nobody completes.
/// </param>
internal sealed record CreateKycFileCommand(
    Guid TenantId,
    Guid CustomerId,
    KycChannel Channel,
    Guid InitiatedBy,
    KycVigilanceLevel VigilanceLevel = KycVigilanceLevel.Standard
) : IRequest<Result<CreateKycFileResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => null;
}

/// <param name="AlreadyExisted">
/// True when an open file was already there. The command succeeds in that case — the caller asked
/// for the customer to have a file and the customer has one — but a consumer uses this to stay
/// silent instead of notifying the agent twice.
/// </param>
internal sealed record CreateKycFileResult(Guid KycFileId, bool AlreadyExisted);
