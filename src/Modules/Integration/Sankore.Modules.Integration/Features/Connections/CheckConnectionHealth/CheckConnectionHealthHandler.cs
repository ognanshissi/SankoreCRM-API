namespace Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class CheckConnectionHealthHandler(
    IntegrationDbContext db,
    IntegrationAdapterResolver resolver,
    TimeProvider clock,
    ILogger<CheckConnectionHealthHandler> logger)
    : IRequestHandler<CheckConnectionHealthCommand, Result<ConnectionHealthDto>>
{
    public async Task<Result<ConnectionHealthDto>> Handle(
        CheckConnectionHealthCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // AsTracking: the outcome is persisted on this row. No tenant predicate — the global
        // query filter scopes it, so another tenant's connection answers 404 and never 403: a
        // health check that could be fired at someone else's connection would also be a way to
        // discover that it exists.
        var connection = await db.Connections
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConnectionId, ct);

        if (connection is null)
            return Result.Fail<ConnectionHealthDto>(IntegrationErrors.ConnectionNotFound);

        var adapter = resolver.ResolveAdapter(connection);

        if (adapter.IsFailure)
        {
            // A clear, distinct answer: the connection is configured but THIS deployment ships no
            // assembly for its kind. Nothing is recorded on the row — the health columns describe
            // the far end's last answer, and writing "unhealthy" there would make the operations
            // screen report an outage at the IMF's CBS over a missing reference in our own build.
            logger.LogWarning(
                "No adapter registered for kind {Kind} (connection {ConnectionId})",
                connection.Kind, connection.Id);

            return Result.Fail<ConnectionHealthDto>(adapter.Code!);
        }

        IntegrationHealth health;

        try
        {
            health = await adapter.Value.CheckHealthAsync(connection, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An adapter is expected to answer Unhealthy rather than throw, but a thrown
            // exception must still leave a trace on the row: otherwise the one thing the operator
            // asked for — "did it answer?" — is the one thing nothing records.
            //
            // The TYPE name only. An exception message from an HTTP stack routinely carries the
            // request URI, and a base URL with a token in it would land in a column that a GET
            // returns.
            logger.LogError(
                ex,
                "Health check of connection {ConnectionId} ({Kind}) threw",
                connection.Id, connection.Kind);

            health = IntegrationHealth.Unhealthy(
                $"The adapter failed with {ex.GetType().Name}. See the server logs for the detail.",
                clock.GetUtcNow());
        }

        connection.RecordHealth(health, clock);
        await db.SaveChangesAsync(ct);

        return Result.Ok(ConnectionHealthDto.From(health));
    }
}
