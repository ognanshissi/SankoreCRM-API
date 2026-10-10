namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Allocating the per-connection sequence number of INT-24 criterion 3 so two concurrent runs
/// cannot take the same one.
///
/// <para>
/// <b>The chosen strategy is insert-and-retry, and the unique index is the authority.</b>
/// <c>ux_integration_batch_file_sequence</c> on <c>(tenant_id, connection_id, direction,
/// sequence_no)</c> is what actually guarantees uniqueness; the <c>MAX(sequence_no) + 1</c> read
/// below is only a good first guess. The alternative — trusting the read — is not a weaker
/// guarantee, it is none at all: two runs that read the same maximum a millisecond apart both
/// compute the same next value, and nothing between the read and the insert stops them.
/// </para>
///
/// <para>
/// Why not allocate inside the transaction with a lock, which the brief offered as the other
/// option: a <c>SELECT … FOR UPDATE</c> has no row to lock for the FIRST file of a connection,
/// so it would need a table-level lock or an advisory lock keyed on the connection — a second
/// mechanism to reason about, serialising every tenant's generation through one more lock the
/// Hangfire job already has an equivalent of. The index exists, it is declared, and catching its
/// violation uses the guarantee that is already there rather than adding one beside it.
/// </para>
///
/// <para>
/// <b>Why a gap is acceptable and a duplicate is not.</b> A retried attempt abandons its number,
/// so a collision leaves a hole in the series. That is fine: the sequence exists so the CBS can
/// tell a gap from a duplicate (see <c>IntegrationBatchFile</c>), and a gap is the benign half —
/// it is visible and investigable. Two files sharing a number is the half that makes a CBS apply
/// one day twice or skip one entirely.
/// </para>
/// </summary>
internal static class BatchSequenceAllocation
{
    /// <summary>
    /// How many numbers one generation will try before giving up. Three is not a tuning: the only
    /// way to lose twice is for two other runs to commit in between, and a connection generates
    /// one file per cut-off — so reaching the third attempt already means something is wrong that
    /// a fourth would not fix.
    /// </summary>
    internal const int MaxAttempts = 3;

    /// <summary>PostgreSQL SQLSTATE for "unique_violation".</summary>
    private const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// The index name as the EF configuration declares it. Referenced, never retyped: a typo here
    /// would turn a genuine write failure into a silently swallowed retry.
    /// </summary>
    internal const string SequenceIndex = "ux_integration_batch_file_sequence";

    /// <summary>
    /// <c>true</c> when <paramref name="ex"/> is this index being violated.
    ///
    /// <para>
    /// Matched on <see cref="PostgresException.ConstraintName"/> where Npgsql reports it and on
    /// the message otherwise, so the branch stays reachable under a provider that surfaces no
    /// <see cref="PostgresException"/> — the same device, and the same reason, as M01's
    /// <c>GroupUniqueViolation</c> and this module's <c>KycLimitAlertUniqueViolation</c>.
    /// </para>
    ///
    /// <para>
    /// Deliberately narrow. A broad "any unique violation is a sequence collision" would retry a
    /// file whose real problem was elsewhere, three times, and then report the collision instead
    /// of the cause.
    /// </para>
    /// </summary>
    internal static bool IsSequenceCollision(this DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState } pg)
        {
            return pg.ConstraintName is null
                   || pg.ConstraintName.Contains(SequenceIndex, StringComparison.OrdinalIgnoreCase);
        }

        return Mentions(ex.Message) || Mentions(ex.InnerException?.Message);
    }

    private static bool Mentions(string? message)
        => message is not null
           && message.Contains(SequenceIndex, StringComparison.OrdinalIgnoreCase);
}
