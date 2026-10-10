namespace Sankore.Modules.Integration.Features.Commands.GetCommand;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One command, in full — for the operator looking at why it is stuck.
/// </summary>
internal sealed record GetIntegrationCommandQuery(Guid CommandId)
    : IRequest<Result<IntegrationCommandDto>>;

/// <summary>
/// <b>There is no payload property on this record, and that is a requirement, not an omission.</b>
///
/// <para>
/// A command payload is a customer's identity document, address and declared income on its way
/// out of the platform; this module encrypts it under its own key precisely so that nothing but
/// the dispatcher ever sees the values. An endpoint that returned it would hand the whole set to
/// anyone holding <c>Integration.Command.View</c> — a permission the specification grants to
/// <c>SalesManager</c> — and would do it outside M01's audited reveal path, which is the one
/// place a document number is allowed to be read and the only place that records who read it.
/// </para>
///
/// <para>
/// <see cref="PayloadFieldNames"/> is what makes the screen useful anyway: "which fields did we
/// send" is answerable, "what were their values" is not. A test asserts this record has no
/// payload property, so the next person to add one has to delete the test first.
/// </para>
/// </summary>
internal sealed record IntegrationCommandDto(
    Guid CommandId,
    Guid ConnectionId,
    string CommandType,
    string EntityType,
    Guid CrmId,
    string IdempotencyKey,
    IReadOnlyList<string> PayloadFieldNames,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string? ErrorFamily,
    string? ErrorCode,
    string? ErrorDetail,
    string? ExternalResponseRef,
    Guid? BatchFileId,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

internal sealed class GetIntegrationCommandHandler(IntegrationDbContext db)
    : IRequestHandler<GetIntegrationCommandQuery, Result<IntegrationCommandDto>>
{
    public async Task<Result<IntegrationCommandDto>> Handle(
        GetIntegrationCommandQuery request, CancellationToken ct)
    {
        // Projected in the query rather than loaded and mapped: the entity carries
        // PayloadEncrypted, and a projection is the cheapest guarantee that the ciphertext never
        // even reaches this process's memory on a read path.
        var row = await db.Commands
            .Where(c => c.Id == request.CommandId)
            .Select(c => new
            {
                c.Id,
                c.ConnectionId,
                c.CommandType,
                c.EntityType,
                c.CrmId,
                c.IdempotencyKey,
                c.PayloadFieldNames,
                c.Status,
                c.Attempts,
                c.NextAttemptAt,
                c.LastErrorFamily,
                c.LastErrorMessage,
                c.ExternalResponseRef,
                c.BatchFileId,
                c.CreatedBy,
                c.CreatedAt,
                c.CompletedAt,
            })
            .FirstOrDefaultAsync(ct);

        // The tenant query filter is what made this null for another tenant's command; the
        // endpoint answers 404, never 403.
        if (row is null) return Result.Fail<IntegrationCommandDto>(IntegrationErrors.CommandNotFound);

        var parts = string.IsNullOrWhiteSpace(row.LastErrorMessage)
            ? []
            : row.LastErrorMessage.Split(':', 2);

        return Result.Ok(new IntegrationCommandDto(
            CommandId: row.Id,
            ConnectionId: row.ConnectionId,
            CommandType: row.CommandType.ToString(),
            EntityType: row.EntityType,
            CrmId: row.CrmId,
            IdempotencyKey: row.IdempotencyKey,
            PayloadFieldNames: string.IsNullOrWhiteSpace(row.PayloadFieldNames)
                ? []
                : row.PayloadFieldNames.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Status: row.Status.ToString(),
            Attempts: row.Attempts,
            NextAttemptAt: row.NextAttemptAt,
            ErrorFamily: row.LastErrorFamily?.ToString(),
            ErrorCode: parts.Length > 0 ? parts[0].Trim() : null,
            ErrorDetail: parts.Length > 1 ? parts[1].Trim() : null,
            ExternalResponseRef: row.ExternalResponseRef,
            BatchFileId: row.BatchFileId,
            CreatedBy: row.CreatedBy,
            CreatedAt: row.CreatedAt,
            CompletedAt: row.CompletedAt));
    }
}
