using MediatR;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetAgency;

public sealed record GetAgencyQuery(Guid AgencyId) : IRequest<Result<AgencyDto>>;
