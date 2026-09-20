namespace Sankore.Modules.Leads.Features.NurturingSequences;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.NurturingSequences.ActivateNurturingSequence;
using Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;
using Sankore.Modules.Leads.Features.NurturingSequences.DeactivateNurturingSequence;
using Sankore.Modules.Leads.Features.NurturingSequences.GetNurturingSequence;
using Sankore.Modules.Leads.Features.NurturingSequences.ListNurturingSequences;
using Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;
using Sankore.Shared.Kernel;

public static class NurturingSequencesEndpoints
{
    public static IEndpointRouteBuilder MapNurturingSequencesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("nurturing-sequences");

        // GET nurturing-sequences
        group.MapGet("", ListSequences)
            .WithName("ListNurturingSequences")
            .WithTags("Nurturing Sequences")
            .RequireAuthorization(Permissions.CanReadNurturingSequences.Code)
            .Produces<IReadOnlyList<NurturingSequenceDto>>()
            .WithOpenApi();

        // GET nurturing-sequences/{id}
        group.MapGet("{id:guid}", GetSequence)
            .WithName("GetNurturingSequence")
            .WithTags("Nurturing Sequences")
            .RequireAuthorization(Permissions.CanReadNurturingSequences.Code)
            .Produces<NurturingSequenceDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST nurturing-sequences
        group.MapPost("", CreateSequence)
            .WithName("CreateNurturingSequence")
            .WithTags("Nurturing Sequences")
            .RequireAuthorization(Permissions.CanManageNurturingSequences.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT nurturing-sequences/{id}
        group.MapPut("{id:guid}", UpdateSequence)
            .WithName("UpdateNurturingSequence")
            .WithTags("Nurturing Sequences")
            .RequireAuthorization(Permissions.CanManageNurturingSequences.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST nurturing-sequences/{id}/activate
        group.MapPost("{id:guid}/activate", ActivateSequence)
            .WithName("ActivateNurturingSequence")
            .WithTags("Nurturing Sequences")
            .RequireAuthorization(Permissions.CanManageNurturingSequences.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST nurturing-sequences/{id}/deactivate
        group.MapPost("{id:guid}/deactivate", DeactivateSequence)
            .WithName("DeactivateNurturingSequence")
            .WithTags("Nurturing Sequences")
            .RequireAuthorization(Permissions.CanManageNurturingSequences.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListSequences(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListNurturingSequencesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetSequence(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetNurturingSequenceQuery(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateSequence(
        CreateNurturingSequenceRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateNurturingSequenceCommand(
            TenantId:    tenant.CurrentTenantId,
            Name:        req.Name,
            Description: req.Description,
            Steps:       req.Steps), ct);

        return result.IsSuccess
            ? Results.Created($"nurturing-sequences/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateSequence(
        Guid id,
        UpdateNurturingSequenceRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateNurturingSequenceCommand(
            SequenceId:  id,
            Name:        req.Name,
            Description: req.Description), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "NURTURING_SEQUENCE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateSequence(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateNurturingSequenceCommand(id), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateSequence(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateNurturingSequenceCommand(id), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }
}

public sealed record CreateNurturingSequenceRequest(
    string Name,
    string? Description,
    IReadOnlyList<StepInput> Steps);

public sealed record UpdateNurturingSequenceRequest(
    string Name,
    string? Description = null);
