namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Text.Json;
using MediatR;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes an immutable audit entry for every command that flows through
/// MediatR (queries are excluded via the ICommand marker to keep the audit
/// table meaningful — reads are not audited by default, only mutations).
///
/// This is what makes the BCEAO compliance requirement ("who did what,
/// when, from where") automatic instead of something each developer must
/// remember to implement per feature.
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
    string? ErrorDetail);

public sealed class AuditBehavior<TRequest, TResponse>(
    IAuditWriter auditWriter,
    ICurrentUser currentUser)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only audit commands (mutations), not queries.
        if (request is not ICommand)
            return await next();

        var response = await next();

        var (outcome, error) = response switch
        {
            Result { IsSuccess: true } => ("SUCCESS", (string?)null),
            Result r => ("FAILURE", r.Error),
            _ => ("SUCCESS", null) // non-Result responses are treated as success
        };

        await auditWriter.WriteAsync(new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            UserId: currentUser.Id,
            TenantId: currentUser.TenantId,
            Action: typeof(TRequest).Name,
            PayloadJson: JsonSerializer.Serialize(request),
            Outcome: outcome,
            ErrorDetail: error),
            cancellationToken);

        return response;
    }
}
