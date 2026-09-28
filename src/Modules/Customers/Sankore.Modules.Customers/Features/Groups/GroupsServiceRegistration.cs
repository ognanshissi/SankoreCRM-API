namespace Sankore.Modules.Customers.Features.Groups;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Groups.Shared;

/// <summary>
/// DI registrations owned by the Groups area. Handlers and validators are picked
/// up by assembly scanning (<c>AddValidatorsFromAssembly(..., includeInternalTypes: true)</c>
/// and the MediatR registration), so only area-specific services belong here.
/// </summary>
public static class GroupsServiceRegistration
{
    internal static IServiceCollection AddGroupsServices(this IServiceCollection s)
    {
        // Scoped, not singleton: it reads tenant settings through ICustomerSettings,
        // which is itself request-scoped.
        s.AddScoped<IGroupSizePolicy, GroupSizePolicy>();

        return s;
    }
}
