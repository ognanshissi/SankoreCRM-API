namespace Sankore.Api.Infrastructure.Audit;

/// <summary>
/// Append-only entity persisted in the <c>audit.entries</c> table.
/// Once written, rows must never be updated or deleted — enforce at DB level:
/// <code>REVOKE UPDATE, DELETE ON audit.entries FROM sankore_app;</code>
///
/// Immutability is expressed in code via <c>init</c>-only setters; all
/// properties are set at construction time and cannot be reassigned.
/// </summary>
public sealed class AuditLogEntry
{
    public Guid Id { get; init; }

    // ── Core audit fields (from AuditBehavior) ────────────────────────────
    public DateTimeOffset Timestamp { get; init; }
    public Guid UserId { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>MediatR command type name, e.g. "CreateAgencyCommand".</summary>
    public string Action { get; init; } = null!;

    /// <summary>Sanitized JSON payload — sensitive fields replaced by "***".</summary>
    public string PayloadJson { get; init; } = null!;

    /// <summary>"SUCCESS" or "FAILURE".</summary>
    public string Outcome { get; init; } = null!;

    public string? ErrorDetail { get; init; }

    // ── Resource enrichment (from IResourceCommand) ───────────────────────
    /// <summary>Domain entity type, e.g. "Agency", "User", "Lead".</summary>
    public string? ResourceType { get; init; }

    /// <summary>Entity primary key as string. Null for create commands.</summary>
    public string? ResourceId { get; init; }

    // ── HTTP context enrichment (from SqlAuditWriter) ─────────────────────
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>ASP.NET Core TraceIdentifier (maps to X-Request-ID header).</summary>
    public string? CorrelationId { get; init; }
}
