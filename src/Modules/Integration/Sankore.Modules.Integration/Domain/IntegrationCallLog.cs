namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One exchange with an external system (INT-08).
///
/// <para>
/// It exists for the BCEAO and CIMA controls: being able to show, call by call, what was asked
/// of a CBS and what it answered. Which is precisely why it carries <b>no payload and no
/// personal data</b> — operation, endpoint, status, duration, error family, correlation id. A
/// journal that held the bodies would be a second copy of the customer database, in clear, kept
/// for years.
/// </para>
///
/// <para>
/// <b>Partitioned monthly on <see cref="At"/>.</b> This is the highest-volume table of the
/// module by an order of magnitude — one row per call, every call — and a monthly partition is
/// what lets an old month be detached and dropped in constant time instead of a DELETE that
/// rewrites the table. PostgreSQL requires the partition key inside every unique constraint, so
/// the primary key is <c>(id, at)</c> and not <c>id</c>.
/// </para>
/// </summary>
public sealed class IntegrationCallLog : AggregateRoot
{
    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The command this call served, when there is one. Null for a read: a live balance lookup
    /// and a health check belong in the journal too, and neither has a command behind it.
    /// </summary>
    public Guid? CommandId { get; private set; }

    /// <summary>Logical operation, e.g. <c>CreateCustomer</c> — not the HTTP method.</summary>
    public string Operation { get; private set; } = string.Empty;

    /// <summary>
    /// The endpoint called, path only. A query string can carry an identifier, and this table is
    /// the one place that must stay safe to export.
    /// </summary>
    public string? Endpoint { get; private set; }

    public int? HttpStatus { get; private set; }

    public long DurationMs { get; private set; }

    /// <summary>Null on success.</summary>
    public ErrorFamily? ErrorFamily { get; private set; }

    /// <summary>Stable code, never a sentence and never a response body.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>
    /// Echoed to the external system and into its logs, so a support request can be followed
    /// across two deployments that share no database.
    /// </summary>
    public string? CorrelationId { get; private set; }

    /// <summary>The partition key. Named <c>at</c> in the schema, as specified.</summary>
    public DateTimeOffset At { get; private set; }

    private IntegrationCallLog() { }

    public static IntegrationCallLog Record(
        Guid tenantId,
        Guid connectionId,
        string operation,
        long durationMs,
        DateTimeOffset at,
        Guid? commandId = null,
        string? endpoint = null,
        int? httpStatus = null,
        ErrorFamily? errorFamily = null,
        string? errorCode = null,
        string? correlationId = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (string.IsNullOrWhiteSpace(operation)) throw new DomainException("An operation is required.");

        return new IntegrationCallLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            CommandId = commandId,
            Operation = operation.Trim(),
            Endpoint = Sanitize(endpoint),
            HttpStatus = httpStatus,
            DurationMs = durationMs < 0 ? 0 : durationMs,
            ErrorFamily = errorFamily,
            ErrorCode = errorCode,
            CorrelationId = correlationId,
            At = at,
        };
    }

    /// <summary>
    /// Drops the query string and the fragment. An adapter that passes a full URL would otherwise
    /// put a customer reference into the one table built to be handed to an auditor.
    /// </summary>
    private static string? Sanitize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var cut = endpoint.IndexOfAny(['?', '#']);
        var path = cut >= 0 ? endpoint[..cut] : endpoint;

        return path.Length <= 500 ? path : path[..500];
    }
}
