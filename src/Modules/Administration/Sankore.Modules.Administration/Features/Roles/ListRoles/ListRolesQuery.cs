using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.ListRoles;

/// <summary>Returns all roles that can be assigned to users.</summary>
public sealed record ListRolesQuery : IRequest<Result<List<RoleDto>>>;

public sealed record RoleDto(Guid Id, string Name, string Label, bool IsSystem, bool IsAssignable);
