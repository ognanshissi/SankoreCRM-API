namespace Sankore.Modules.Customers.Features.Groups.Shared;

using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Recognises the unique-index violation that backs
/// <c>GROUP_NAME_ALREADY_USED</c> (<c>ux_client_groups_name</c> on
/// <c>(tenant_id, agency_id, name)</c>).
///
/// The in-database check is the only race-proof one: two concurrent creations
/// both pass the <c>AnyAsync</c> pre-check, and one of them then fails on
/// commit. The handler turns that failure into the same business code instead of
/// a 500.
/// </summary>
internal static class GroupUniqueViolation
{
    /// <summary>PostgreSQL SQLSTATE for "unique_violation".</summary>
    private const string UniqueViolationSqlState = "23505";

    internal const string GroupNameIndex = "ux_client_groups_name";
    internal const string ActiveMembershipIndex = "ux_group_memberships_active";

    /// <summary>
    /// True when <paramref name="ex"/> is the unique violation of
    /// <paramref name="indexName"/>. Falls back to a message match so that the
    /// code path stays reachable under providers that do not surface a
    /// <see cref="PostgresException"/> (EF InMemory in tests, a future provider).
    /// </summary>
    internal static bool IsViolationOf(this DbUpdateException ex, string indexName)
    {
        if (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState } pg)
        {
            return pg.ConstraintName is null
                   || pg.ConstraintName.Contains(indexName, StringComparison.OrdinalIgnoreCase);
        }

        return Mentions(ex.Message, indexName) || Mentions(ex.InnerException?.Message, indexName);
    }

    private static bool Mentions(string? message, string indexName)
        => message is not null && message.Contains(indexName, StringComparison.OrdinalIgnoreCase);
}
