namespace Sankore.Modules.Leads.Features.SlaConfigs;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.SlaConfigs.ActivateSlaConfig;
using Sankore.Modules.Leads.Features.SlaConfigs.CreateSlaConfig;
using Sankore.Modules.Leads.Features.SlaConfigs.DeactivateSlaConfig;
using Sankore.Modules.Leads.Features.SlaConfigs.GetSlaConfig;
using Sankore.Modules.Leads.Features.SlaConfigs.ListSlaConfigs;
using Sankore.Modules.Leads.Features.SlaConfigs.UpdateSlaConfig;
using Sankore.Shared.Kernel;

public static class SlaConfigsEndpoints
{
    public static IEndpointRouteBuilder MapSlaConfigsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("sla-configs");

        // GET sla-configs
        group.MapGet("", ListConfigs)
            .WithName("ListSlaConfigs")
            .WithTags("SLA Configs")
            .RequireAuthorization("lead:sla-config:read")
            .Produces<IReadOnlyList<SlaConfigDto>>()
            .WithOpenApi();

        // GET sla-configs/{id}
        group.MapGet("{slaConfigId:guid}", GetConfig)
            .WithName("GetSlaConfig")
            .WithTags("SLA Configs")
            .RequireAuthorization("lead:sla-config:read")
            .Produces<SlaConfigDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST sla-configs
        group.MapPost("", CreateConfig)
            .WithName("CreateSlaConfig")
            .WithTags("SLA Configs")
            .RequireAuthorization("lead:sla-config:manage")
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT sla-configs/{id}
        group.MapPut("{slaConfigId:guid}", UpdateConfig)
            .WithName("UpdateSlaConfig")
            .WithTags("SLA Configs")
            .RequireAuthorization("lead:sla-config:manage")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST sla-configs/{id}/activate
        group.MapPost("{slaConfigId:guid}/activate", ActivateConfig)
            .WithName("ActivateSlaConfig")
            .WithTags("SLA Configs")
            .RequireAuthorization("lead:sla-config:manage")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST sla-configs/{id}/deactivate
        group.MapPost("{slaConfigId:guid}/deactivate", DeactivateConfig)
            .WithName("DeactivateSlaConfig")
            .WithTags("SLA Configs")
            .RequireAuthorization("lead:sla-config:manage")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListConfigs(
        ISender sender, CancellationToken ct,
        bool? activeOnly = null, Guid? agencyId = null)
    {
        var result = await sender.Send(
            new ListSlaConfigsQuery(activeOnly, agencyId), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetConfig(
        Guid slaConfigId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetSlaConfigQuery(slaConfigId), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateConfig(
        CreateSlaConfigRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateSlaConfigCommand(
            TenantId:              tenant.CurrentTenantId,
            AgencyId:              req.AgencyId,
            Name:                  req.Name,
            FirstContactDeadline:  req.FirstContactDeadline,
            QualificationDeadline: req.QualificationDeadline,
            FollowUpDeadline:      req.FollowUpDeadline,
            EscalationDeadline:    req.EscalationDeadline), ct);

        return result.IsSuccess
            ? Results.Created($"leads/sla-configs/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateConfig(
        Guid slaConfigId,
        UpdateSlaConfigRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateSlaConfigCommand(
            SlaConfigId:           slaConfigId,
            Name:                  req.Name,
            FirstContactDeadline:  req.FirstContactDeadline,
            QualificationDeadline: req.QualificationDeadline,
            FollowUpDeadline:      req.FollowUpDeadline,
            EscalationDeadline:    req.EscalationDeadline), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "SLA_CONFIG_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateConfig(
        Guid slaConfigId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateSlaConfigCommand(slaConfigId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateConfig(
        Guid slaConfigId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateSlaConfigCommand(slaConfigId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }
}

public sealed record CreateSlaConfigRequest(
    string Name,
    TimeSpan FirstContactDeadline,
    TimeSpan QualificationDeadline,
    TimeSpan FollowUpDeadline,
    TimeSpan EscalationDeadline,
    Guid? AgencyId = null);

public sealed record UpdateSlaConfigRequest(
    string Name,
    TimeSpan FirstContactDeadline,
    TimeSpan QualificationDeadline,
    TimeSpan FollowUpDeadline,
    TimeSpan EscalationDeadline);
