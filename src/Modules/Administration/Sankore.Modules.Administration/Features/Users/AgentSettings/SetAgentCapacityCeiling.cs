using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AgentSettings;

public sealed record SetAgentCapacityCeilingRequest(int CapacityCeiling);

/// <summary>Le plafond de capacité d'un agent, fixé par son supérieur ou un administrateur.</summary>
public sealed record SetAgentCapacityCeilingCommand(Guid UserId, int CapacityCeiling)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}

internal sealed class SetAgentCapacityCeilingValidator : AbstractValidator<SetAgentCapacityCeilingCommand>
{
    public SetAgentCapacityCeilingValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.CapacityCeiling).InclusiveBetween(1, Domain.AgentSettings.MaxCapacityCeiling);
    }
}

internal sealed class SetAgentCapacityCeilingHandler(AdministrationDbContext db)
    : IRequestHandler<SetAgentCapacityCeilingCommand, Result>
{
    public async Task<Result> Handle(SetAgentCapacityCeilingCommand cmd, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct);
        if (user is null)
            return Result.Fail("USER_NOT_FOUND");

        var settings = await db.AgentSettings.AsTracking().FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (settings is null)
        {
            settings = Domain.AgentSettings.Create(user.TenantId, user.Id);
            db.AgentSettings.Add(settings);
        }

        try
        {
            settings.SetCapacityCeiling(cmd.CapacityCeiling);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal static class SetAgentCapacityCeilingEndpoint
{
    internal static IEndpointRouteBuilder MapSetAgentCapacityCeiling(this IEndpointRouteBuilder app)
    {
        app.MapPut("{userId:guid}/agent-settings/ceiling", async (
                Guid userId, SetAgentCapacityCeilingRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new SetAgentCapacityCeilingCommand(userId, req.CapacityCeiling), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("SetAgentCapacityCeiling")
            .WithSummary("Fixe le plafond de leads actifs d'un agent. Une capacité déclarée au-dessus est ramenée au plafond.")
            .WithTags("Users")
            .RequireAuthorization(Permissions.CanUpdateUser.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
