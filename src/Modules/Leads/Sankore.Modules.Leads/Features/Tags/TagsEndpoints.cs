namespace Sankore.Modules.Leads.Features.Tags;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Tags.AddTag;
using Sankore.Modules.Leads.Features.Tags.ListTags;
using Sankore.Modules.Leads.Features.Tags.RemoveTag;
using Sankore.Shared.Kernel;

public static class TagsEndpoints
{
    public static IEndpointRouteBuilder MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("{leadId:guid}/tags");

        // GET leads/{leadId}/tags
        group.MapGet("", ListTags)
            .WithName("ListLeadTags")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<TagDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST leads/{leadId}/tags
        group.MapPost("", AddTag)
            .WithName("AddLeadTag")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanTagLead.Code)
            .Produces<TagDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // DELETE leads/{leadId}/tags/{tag}
        group.MapDelete("{tag}", RemoveTag)
            .WithName("RemoveLeadTag")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanTagLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListTags(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListTagsQuery(leadId), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> AddTag(
        Guid leadId,
        AddTagRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new AddTagCommand(leadId, req.Tag, req.AddedBy), ct);

        return result.IsSuccess
            ? Results.Created($"leads/{leadId}/tags/{result.Value!.Tag}", result.Value)
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Add tag failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> RemoveTag(
        Guid leadId, string tag, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RemoveTagCommand(leadId, tag), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "TAG_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Remove tag failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record AddTagRequest(string Tag, Guid AddedBy);
