using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.UpdateUser;

internal sealed class UpdateUserHandler(
    AdministrationDbContext db
) : IRequestHandler<UpdateUserCommand, Result>
{
    public async Task<Result> Handle(UpdateUserCommand request, CancellationToken ct)
    {
        var user = await db.Users
            .AsTracking()
            .Where(u => u.Id == request.UserId)
            .SingleOrDefaultAsync(ct);

        if (user is null)
            return Result.Fail($"User {request.UserId} not found.");

        if (request.AgencyId.HasValue)
        {
            var agencyExists = await db.Agencies
                .AnyAsync(a => a.Id == request.AgencyId.Value, ct);
            if (!agencyExists)
                return Result.Fail($"Agency {request.AgencyId} not found.");
        }

        user.UpdateDetails(
            request.FullName,
            request.AgencyId,
            request.SpokenLanguages,
            request.Specialties,
            request.EnableNotifications);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
