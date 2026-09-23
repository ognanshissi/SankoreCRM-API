namespace Sankore.Modules.Leads.Features.LeadSources.GetSourceMetadata;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;

public static class GetSourceMetadataEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceMetadata(this IEndpointRouteBuilder app)
    {
        app.MapGet("lead-sources/metadata", Handle)
            .WithName("GetLeadSourceMetadata")
            .WithTags("LeadSources")
            .WithSummary("Returns channels, modes and their allowed combinations for the UI")
            .RequireAuthorization()
            .Produces<SourceMetadataResponse>()
            .WithOpenApi();

        return app;
    }

    private static IResult Handle()
    {
        var channelModes = ChannelModeMap.GetAll();

        var channels = Enum.GetValues<LeadChannelType>()
            .Select(ch => new ChannelMetadata(
                Code: ch.ToString(),
                AllowedModes: channelModes.TryGetValue(ch, out var modes)
                    ? modes.Select(m => m.ToString()).ToList()
                    : [],
                DefaultMode: ChannelModeMap.GetDefaultMode(ch)?.ToString()))
            .ToList();

        var response = new SourceMetadataResponse(
            Channels: channels,
            Modes: Enum.GetValues<IntegrationMode>().Select(m => m.ToString()).ToList());

        return Results.Ok(response);
    }
}

public sealed record SourceMetadataResponse(
    IReadOnlyList<ChannelMetadata> Channels,
    IReadOnlyList<string> Modes);

public sealed record ChannelMetadata(
    string Code,
    IReadOnlyList<string> AllowedModes,
    string? DefaultMode);
