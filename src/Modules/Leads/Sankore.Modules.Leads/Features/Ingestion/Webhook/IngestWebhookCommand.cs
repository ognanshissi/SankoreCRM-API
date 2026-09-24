namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Processes an authenticated webhook push — single or batch (US-F13.37-BE-17).
/// The endpoint handles HTTP concerns (signature, IP, body reading);
/// this command handles domain logic (source resolution, parsing, batching, ingestion).
/// </summary>
internal sealed record IngestWebhookCommand(
    string PublicKey,
    string Body,
    string? SignatureHeader,
    string RemoteIp
) : IRequest<Result<IngestWebhookResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookIngestion";
    public string? ResourceId  => null;
}

internal sealed record IngestWebhookResult(
    int StatusCode,
    IReadOnlyList<WebhookItemResult> Items);

public sealed record WebhookItemResult(
    Guid? IngestionId,
    Guid? LeadId,
    string Status,
    string? Error);
