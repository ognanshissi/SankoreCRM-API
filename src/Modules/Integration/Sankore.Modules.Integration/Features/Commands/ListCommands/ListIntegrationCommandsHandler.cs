namespace Sankore.Modules.Integration.Features.Commands.ListCommands;

using Microsoft.EntityFrameworkCore;
using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListIntegrationCommandsHandler(IntegrationDbContext db)
    : IRequestHandler<ListIntegrationCommandsQuery, Result<IntegrationCommandListPage>>
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public async Task<Result<IntegrationCommandListPage>> Handle(
        ListIntegrationCommandsQuery request, CancellationToken ct)
    {
        var filters = ParseFilters(request);
        if (filters.IsFailure) return Result.Fail<IntegrationCommandListPage>(filters.Error!);

        var (status, family, commandType) = filters.Value;

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(
            request.PageSize <= 0 ? DefaultPageSize : request.PageSize, 1, MaxPageSize);

        // The tenant filter is the DbContext's: this read only ever serves an HTTP caller, and a
        // command of another tenant must be invisible rather than refused.
        var query = db.Commands.AsQueryable();

        if (status is { } s) query = query.Where(c => c.Status == s);
        if (family is { } f) query = query.Where(c => c.LastErrorFamily == f);
        if (commandType is { } t) query = query.Where(c => c.CommandType == t);
        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            var entityType = request.EntityType.Trim();
            query = query.Where(c => c.EntityType == entityType);
        }

        if (request.CrmId is { } crmId) query = query.Where(c => c.CrmId == crmId);
        if (request.ConnectionId is { } connectionId) query = query.Where(c => c.ConnectionId == connectionId);
        if (request.From is { } from) query = query.Where(c => c.CreatedAt >= from);
        if (request.To is { } to) query = query.Where(c => c.CreatedAt <= to);

        var totalCount = await query.CountAsync(ct);

        var rows = await query
            // Oldest first — the opposite of a feed, on purpose. This is a work queue, and the
            // command that has been stuck longest is the one that costs; it is also the order
            // INT-06 replays a customer's commands in.
            .OrderBy(c => c.CreatedAt)
            // CreatedAt is not unique (a lead import queues a hundred in the same millisecond),
            // and without a tie-break two pages can repeat or skip a row.
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.ConnectionId,
                c.CommandType,
                c.EntityType,
                c.CrmId,
                c.Status,
                c.Attempts,
                c.NextAttemptAt,
                c.LastErrorFamily,
                c.LastErrorMessage,
                c.ExternalResponseRef,
                c.CreatedAt,
                c.CompletedAt,
            })
            .ToListAsync(ct);

        return Result.Ok(new IntegrationCommandListPage(
            Rows:
            [
                .. rows.Select(r =>
                {
                    var (code, detail) = SplitError(r.LastErrorMessage);

                    return new IntegrationCommandListItem(
                        CommandId: r.Id,
                        ConnectionId: r.ConnectionId,
                        CommandType: r.CommandType.ToString(),
                        EntityType: r.EntityType,
                        CrmId: r.CrmId,
                        Status: r.Status.ToString(),
                        Attempts: r.Attempts,
                        NextAttemptAt: r.NextAttemptAt,
                        ErrorFamily: r.LastErrorFamily?.ToString(),
                        ErrorCode: code,
                        ErrorDetail: detail,
                        ExternalResponseRef: r.ExternalResponseRef,
                        CreatedAt: r.CreatedAt,
                        CompletedAt: r.CompletedAt);
                })
            ],
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize));
    }

    /// <summary>
    /// The aggregate stores one column as <c>CODE: detail</c> (bounded, code first so a long
    /// detail cannot push the code out of the row). Split in memory and not in SQL: it is string
    /// work over a page's worth of rows, and pushing it down would buy a <c>split_part</c> that
    /// no index helps.
    /// </summary>
    private static (string? Code, string? Detail) SplitError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return (null, null);

        var parts = message.Split(':', 2);
        return parts.Length == 2
            ? (parts[0].Trim(), parts[1].Trim())
            : (message.Trim(), null);
    }

    /// <summary>
    /// Filters arrive as text, so an unknown value is a caller's typo and not a 500 — and it is
    /// refused by name, listing what is accepted.
    /// </summary>
    private static Result<(CommandStatus? Status, PublicApi.ErrorFamily? Family, CommandType? Type)>
        ParseFilters(ListIntegrationCommandsQuery request)
    {
        CommandStatus? status = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<CommandStatus>(request.Status, ignoreCase: true, out var parsed))
                return Fail($"INTEGRATION_COMMAND_STATUS_UNKNOWN: '{request.Status}' is not a command "
                            + $"status ({string.Join(", ", Enum.GetNames<CommandStatus>())}).");

            status = parsed;
        }

        PublicApi.ErrorFamily? family = null;
        if (!string.IsNullOrWhiteSpace(request.ErrorFamily))
        {
            if (!Enum.TryParse<PublicApi.ErrorFamily>(request.ErrorFamily, ignoreCase: true, out var parsed))
                return Fail($"INTEGRATION_ERROR_FAMILY_UNKNOWN: '{request.ErrorFamily}' is not an error "
                            + $"family ({string.Join(", ", Enum.GetNames<PublicApi.ErrorFamily>())}).");

            family = parsed;
        }

        CommandType? commandType = null;
        if (!string.IsNullOrWhiteSpace(request.CommandType))
        {
            if (!Enum.TryParse<CommandType>(request.CommandType, ignoreCase: true, out var parsed))
                return Fail($"INTEGRATION_COMMAND_TYPE_UNKNOWN: '{request.CommandType}' is not a command "
                            + $"type ({string.Join(", ", Enum.GetNames<CommandType>())}).");

            commandType = parsed;
        }

        return Result.Ok((status, family, commandType));
    }

    private static Result<(CommandStatus?, PublicApi.ErrorFamily?, CommandType?)> Fail(string error)
        => Result.Fail<(CommandStatus?, PublicApi.ErrorFamily?, CommandType?)>(error);
}
