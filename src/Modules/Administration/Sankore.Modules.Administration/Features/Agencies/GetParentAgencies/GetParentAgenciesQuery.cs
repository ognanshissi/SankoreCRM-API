using MediatR;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetParentAgencies;

/// <summary>Returns agencies that are referenced as a parent by at least one other agency.</summary>
public sealed record GetParentAgenciesQuery : IRequest<Result<List<AgencyDto>>>;
