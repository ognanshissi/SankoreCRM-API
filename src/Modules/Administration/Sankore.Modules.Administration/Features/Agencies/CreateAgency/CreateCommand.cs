using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

public sealed record CreateAgencyCommand(
    string Name,
    string Description,
    AgencyType AgencyType,
    Guid? ParentAgencyId,
    string? AddressStreet,
    string? AddressCity,
    string? AddressState,
    string? AddressCountry,
    string? AddressZipCode,
    double? Latitude,
    double? Longitude
) : IRequest<Result<CreateAgencyResult>>, ICommand;

public sealed record CreateAgencyResult(Guid AgencyId, string Code);