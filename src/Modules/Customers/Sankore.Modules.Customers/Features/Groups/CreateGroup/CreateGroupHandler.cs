namespace Sankore.Modules.Customers.Features.Groups.CreateGroup;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class CreateGroupHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<CreateGroupCommand, Result<CreateGroupResult>>
{
    public async Task<Result<CreateGroupResult>> Handle(CreateGroupCommand request, CancellationToken ct)
    {
        var name = request.Name.Trim();

        // Two barriers guard the same rule, deliberately:
        //  1. this read, which turns the common case into a clean business code;
        //  2. ux_client_groups_name, which is the only one that survives two
        //     concurrent requests (caught below).
        var nameTaken = await db.ClientGroups
            .AnyAsync(g => g.AgencyId == request.AgencyId && g.Name == name, ct);

        if (nameTaken)
            return Result.Fail<CreateGroupResult>(CustomerErrors.GroupNameAlreadyUsed);

        var group = ClientGroup.Create(
            tenantId: currentUser.TenantId,
            type: request.Type,
            name: name,
            agencyId: request.AgencyId,
            constitutionDate: request.ConstitutionDate,
            createdBy: currentUser.Id);

        db.ClientGroups.Add(group);

        // Published BEFORE SaveChangesAsync: the outbox row and the group row must
        // commit in the same transaction, or a broker outage would lose the event.
        await publisher.PublishAsync(
            new GroupCreatedEvent(
                TenantId: group.TenantId,
                GroupId: group.Id,
                GroupType: group.Type.ToString(),
                Name: group.Name,
                AgencyId: group.AgencyId),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsViolationOf(GroupUniqueViolation.GroupNameIndex))
        {
            // Lost the race against a concurrent creation of the same name in the
            // same agency: same business outcome as the pre-check above.
            return Result.Fail<CreateGroupResult>(CustomerErrors.GroupNameAlreadyUsed);
        }

        return Result.Ok(new CreateGroupResult(
            group.Id, group.Name, group.Type.ToString(), group.Status.ToString()));
    }
}
