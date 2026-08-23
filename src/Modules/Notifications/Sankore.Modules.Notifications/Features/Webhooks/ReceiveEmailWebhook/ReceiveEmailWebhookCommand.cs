namespace Sankore.Modules.Notifications.Features.Webhooks.ReceiveEmailWebhook;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Not marked ICommand — webhook calls are anonymous (no JWT / ICurrentUser).
/// TransactionBehavior and AuditBehavior are skipped intentionally.
/// </summary>
internal sealed record ReceiveEmailWebhookCommand(
    Guid TenantId,
    string Provider,
    string RawBody)
    : IRequest<Result>;
