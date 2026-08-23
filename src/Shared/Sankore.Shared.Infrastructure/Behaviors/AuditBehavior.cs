namespace Sankore.Shared.Infrastructure.Behaviors;

using MediatR;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes an immutable audit entry for every command that flows through
/// MediatR (queries are excluded via the ICommand marker to keep the audit
/// table meaningful — reads are not audited by default, only mutations).
///
/// Payload is serialized via <see cref="SanitizedJsonSerializer"/> so that
/// properties marked with <see cref="SensitiveDataAttribute"/>
/// (passwords, tokens, etc.) are replaced with "***" before being stored.
///
/// When the command also implements <see cref="IResourceCommand"/>, the
/// <c>ResourceType</c> and <c>ResourceId</c> fields are populated so that
/// the audit log can be filtered by affected entity.
/// </summary>
public interface ICommand
{
    // Marker interface: commands that mutate state implement this so the
    // AuditBehavior and TransactionBehavior only wrap the requests that
    // actually need them, not read-only queries.
}

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    Guid UserId,
    Guid TenantId,
    string Action,
    string PayloadJson,
    string Outcome,
    string? ErrorDetail,
    string? ResourceType,
    string? ResourceId);

public sealed class AuditBehavior<TRequest, TResponse>(
    IAuditWriter auditWriter,
    ICurrentUser currentUser,
    ITenantContext tenant)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ICommand)
            return await next();

        var response = await next();

        var (outcome, error) = response switch
        {
            Result { IsSuccess: true } => ("SUCCESS", (string?)null),
            Result r => ("FAILURE", r.Error),
            _ => ("SUCCESS", null)
        };

        string? resourceType = null;
        string? resourceId = null;
        if (request is IResourceCommand resourceCommand)
        {
            resourceType = resourceCommand.ResourceType;
            resourceId = resourceCommand.ResourceId;
        }

        await auditWriter.WriteAsync(new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            UserId: currentUser.Id,
            TenantId: tenant.CurrentTenantId,
            Action: typeof(TRequest).Name,
            PayloadJson: SanitizedJsonSerializer.Serialize(request),
            Outcome: outcome,
            ErrorDetail: error,
            ResourceType: resourceType,
            ResourceId: resourceId),
            cancellationToken);

        return response;
    }
}
