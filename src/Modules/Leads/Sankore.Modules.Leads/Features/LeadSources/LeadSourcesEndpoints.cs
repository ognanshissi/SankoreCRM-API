namespace Sankore.Modules.Leads.Features.LeadSources;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.DeactivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.GetSourceMetadata;
using Sankore.Modules.Leads.Features.LeadSources.GetLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;
using Sankore.Modules.Leads.Features.LeadSources.MarkErrorLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.PauseLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.PreviewMapping;
using Sankore.Modules.Leads.Features.LeadSources.RotateHmacSecret;
using Sankore.Modules.Leads.Features.LeadSources.RotatePublicKey;
using Sankore.Modules.Leads.Features.LeadSources.SetSecret;
using Sankore.Modules.Leads.Features.LeadSources.Snippet;
using Sankore.Modules.Leads.Features.LeadSources.StartTestingLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;
using Sankore.Shared.Kernel;

public static class LeadSourcesEndpoints
{
    public static IEndpointRouteBuilder MapLeadSourcesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("lead-sources");

        group.MapGet("", ListSources)
            .WithName("ListLeadSources")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces<PagedResult<LeadSourceListDto>>()
            .WithOpenApi();

        group.MapGet("{id:guid}", GetSource)
            .WithName("GetLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces<LeadSourceDetailDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("", CreateSource)
            .WithName("CreateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPut("{id:guid}", UpdateSource)
            .WithName("UpdateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("{id:guid}/start-testing", StartTestingSource)
            .WithName("StartTestingLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPost("{id:guid}/activate", ActivateSource)
            .WithName("ActivateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPost("{id:guid}/pause", PauseSource)
            .WithName("PauseLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPost("{id:guid}/mark-error", MarkErrorSource)
            .WithName("MarkErrorLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPost("{id:guid}/archive", ArchiveSource)
            .WithName("ArchiveLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST /lead-sources/{id}/mapping/preview — dry-run mapping against sample payload
        group.MapPost("{id:guid}/mapping/preview", PreviewMapping)
            .WithName("PreviewLeadSourceMapping")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<PreviewMappingResult>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // ── Secrets management (US-F13.37-BE-09) ──────────────────────────
        group.MapPut("{id:guid}/secrets/{name}", SetSourceSecret)
            .WithName("SetLeadSourceSecret")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSourceCredentials.Code)
            .Produces<SecretHint>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("{id:guid}/secrets/hmac/rotate", RotateHmac)
            .WithName("RotateLeadSourceHmacSecret")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSourceCredentials.Code)
            .Produces<RotateHmacSecretResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("{id:guid}/public-key/rotate", RotateSourcePublicKey)
            .WithName("RotateLeadSourcePublicKey")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSourceCredentials.Code)
            .Produces<string>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // ── Snippet (US-F13.37-BE-14) ────────────────────────────────────
        group.MapGet("{id:guid}/snippet", GetSnippet)
            .WithName("GetLeadSourceSnippet")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<SnippetResult>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("{id:guid}/snippet/send", SendSnippetEmail)
            .WithName("SendLeadSourceSnippet")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // Metadata — channels, modes, allowed combinations
        app.MapGetSourceMetadata();

        return app;
    }

    private static async Task<IResult> ListSources(
        ISender sender, CancellationToken ct,
        LeadChannelType? channelType = null,
        IntegrationMode? mode = null,
        LeadSourceStatus? status = null,
        string? q = null,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(
            new ListLeadSourcesQuery(channelType, mode, status, q, page, pageSize), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetSource(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetLeadSourceQuery(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateSource(
        CreateLeadSourceRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateLeadSourceCommand(
            TenantId:        tenant.CurrentTenantId,
            Code:            req.Code,
            Label:           req.Label,
            ChannelType:     req.ChannelType,
            DisplayOrder:    req.DisplayOrder,
            IntegrationMode: req.IntegrationMode,
            Description:     req.Description,
            Settings:        req.Settings,
            PlatformConnectionId: req.PlatformConnectionId,
            DedupWindowDays: req.DedupWindowDays,
            CostPerLead:     req.CostPerLead,
            CostCurrency:    req.CostCurrency), ct);

        return result.IsSuccess
            ? Results.Created($"lead-sources/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateSource(
        Guid id, UpdateLeadSourceRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateLeadSourceCommand(
            SourceId:        id,
            ExpectedVersion: req.Version,
            Label:           req.Label,
            DisplayOrder:    req.DisplayOrder,
            Description:     req.Description,
            Settings:        req.Settings,
            PlatformConnectionId: req.PlatformConnectionId,
            DedupWindowDays: req.DedupWindowDays,
            CostPerLead:     req.CostPerLead,
            CostCurrency:    req.CostCurrency), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "LEAD_SOURCE_NOT_FOUND" => Results.NotFound(),
                "CONFLICT" => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(detail: result.Error, statusCode: 422)
            };
    }

    private static async Task<IResult> GetSnippet(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetSnippetQuery(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error == "NOT_EMBEDDED_SCRIPT_MODE"
                ? Results.NotFound()
                : result.Error == "SOURCE_NOT_FOUND"
                    ? Results.NotFound()
                    : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> SendSnippetEmail(
        Guid id, SendSnippetRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SendSnippetCommand(id, req.Email), ct);
        return result.IsSuccess
            ? Results.Accepted()
            : result.Error is "SOURCE_NOT_FOUND" or "NOT_EMBEDDED_SCRIPT_MODE"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> StartTestingSource(Guid id, ISender sender, CancellationToken ct)
        => await LifecycleTransition(sender.Send(new StartTestingLeadSourceCommand(id), ct));

    private static async Task<IResult> ActivateSource(Guid id, ISender sender, CancellationToken ct)
        => await LifecycleTransition(sender.Send(new ActivateLeadSourceCommand(id), ct));

    private static async Task<IResult> PauseSource(Guid id, ISender sender, CancellationToken ct)
        => await LifecycleTransition(sender.Send(new PauseLeadSourceCommand(id), ct));

    private static async Task<IResult> MarkErrorSource(Guid id, ISender sender, CancellationToken ct)
        => await LifecycleTransition(sender.Send(new MarkErrorLeadSourceCommand(id), ct));

    private static async Task<IResult> ArchiveSource(Guid id, ISender sender, CancellationToken ct)
        => await LifecycleTransition(sender.Send(new DeactivateLeadSourceCommand(id), ct));

    private static async Task<IResult> PreviewMapping(
        Guid id, PreviewMappingRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new PreviewMappingQuery(id, req.SamplePayloadJson), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error switch
            {
                "SOURCE_NOT_FOUND" => Results.NotFound(new { error = result.Error }),
                _ => Results.UnprocessableEntity(new { error = result.Error })
            };
    }

    private static async Task<IResult> SetSourceSecret(
        Guid id, string name, SetSecretRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new SetSecretCommand(id, name, req.Value, req.ExpiresAt), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error == "LEAD_SOURCE_NOT_FOUND" ? Results.NotFound() : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> RotateHmac(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RotateHmacSecretCommand(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error == "LEAD_SOURCE_NOT_FOUND" ? Results.NotFound() : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> RotateSourcePublicKey(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RotatePublicKeyCommand(id), ct);
        return result.IsSuccess
            ? Results.Ok(new { publicKey = result.Value })
            : result.Error == "LEAD_SOURCE_NOT_FOUND" ? Results.NotFound() : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> LifecycleTransition(Task<Result> task)
    {
        var result = await task;
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_SOURCE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }
}

public sealed record CreateLeadSourceRequest(
    string Code,
    string Label,
    LeadChannelType ChannelType,
    int DisplayOrder,
    IntegrationMode? IntegrationMode = null,
    string? Description = null,
    SourceSettings? Settings = null,
    string? PlatformConnectionId = null,
    int DedupWindowDays = 30,
    decimal? CostPerLead = null,
    string? CostCurrency = null);

public sealed record UpdateLeadSourceRequest(
    uint Version,
    string Label,
    int DisplayOrder,
    string? Description = null,
    SourceSettings? Settings = null,
    string? PlatformConnectionId = null,
    int? DedupWindowDays = null,
    decimal? CostPerLead = null,
    string? CostCurrency = null);

public sealed record PreviewMappingRequest(string SamplePayloadJson);

public sealed record SetSecretRequest(string Value, DateTimeOffset? ExpiresAt = null);

public sealed record SendSnippetRequest(string Email);
