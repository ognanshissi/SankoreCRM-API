using MediatR;
using Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;

internal sealed class ListPermissionsHandler
    : IRequestHandler<ListPermissionsQuery, Result<List<PermissionGroupDto>>>
{
    public Task<Result<List<PermissionGroupDto>>> Handle(
        ListPermissionsQuery request, CancellationToken ct)
    {
        var source = Shared.Kernel.Permissions.All.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(request.Module))
            source = source.Where(p =>
                p.Module.Equals(request.Module, StringComparison.OrdinalIgnoreCase));

        var groups = source
            .GroupBy(p => p.Module)
            .OrderBy(g => g.Key)
            .Select(g => new PermissionGroupDto(
                g.Key,
                g.OrderBy(p => p.Action)
                 .Select(p => new PermissionDto(p.Code, p.Description, p.Action))
                 .ToList()))
            .ToList();

        return Task.FromResult(Result.Ok(groups));
    }
}
