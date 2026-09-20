namespace Sankore.Modules.Leads.Features.NurturingSequences.ListNurturingSequences;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListNurturingSequencesQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<NurturingSequenceDto>>>;
