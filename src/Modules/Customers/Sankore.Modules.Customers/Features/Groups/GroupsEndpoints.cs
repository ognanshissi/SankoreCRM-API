namespace Sankore.Modules.Customers.Features.Groups;

using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Groups.AddGroupMember;
using Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;
using Sankore.Modules.Customers.Features.Groups.CreateGroup;
using Sankore.Modules.Customers.Features.Groups.DissolveGroup;
using Sankore.Modules.Customers.Features.Groups.GetGroup;
using Sankore.Modules.Customers.Features.Groups.ListGroups;
using Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;
using Sankore.Modules.Customers.Features.Groups.SuspendGroup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Route aggregator of the Groups area (US-M01-BE-19/20/21), mounted under
/// <c>api/v1/client-groups</c>. Adding a slice means one file per feature plus
/// one <c>Map…()</c> call below — nothing else in the module changes.
/// </summary>
public static class GroupsEndpoints
{
    public static IEndpointRouteBuilder MapGroupsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("client-groups").WithTags("Client Groups");

        return group
            .MapCreateGroup()
            .MapListGroups()
            .MapGetGroup()
            .MapAddGroupMember()
            .MapRemoveGroupMember()
            .MapAssignOfficeRole()
            .MapSuspendGroup()
            .MapDissolveGroup();
    }
}
