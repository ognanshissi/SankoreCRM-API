namespace Sankore.Modules.Leads.Features.FindDuplicates;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record FindDuplicatesQuery(
    string? PhoneNumber,
    string? Email
) : IRequest<Result<IReadOnlyList<LeadDuplicateDto>>>;
