namespace Sankore.Modules.Leads.Features.NurturingSequences.GetNurturingSequence;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetNurturingSequenceQuery(Guid SequenceId)
    : IRequest<Result<NurturingSequenceDto>>;
