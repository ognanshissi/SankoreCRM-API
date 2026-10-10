namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Xunit;

/// <summary>
/// The collision recogniser that makes criterion 3's "unique per connection" a retry rather than
/// a crash.
///
/// <para>
/// Tested on its own because the EF InMemory provider enforces no unique index — so no test in
/// this folder can make a real collision happen, and the only honest thing to pin is that the
/// violation IS recognised when PostgreSQL raises it, and that nothing else is mistaken for one.
/// A recogniser that was too broad would retry a file whose real problem was elsewhere and then
/// report a collision instead of the cause.
/// </para>
/// </summary>
public sealed class BatchSequenceAllocationTests
{
    [Fact]
    public void A_violation_of_the_sequence_index_is_recognised()
    {
        var ex = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            Violation(BatchSequenceAllocation.SequenceIndex));

        ex.IsSequenceCollision().Should().BeTrue();
    }

    [Fact]
    public void A_violation_of_another_index_is_not()
    {
        var ex = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            Violation("ux_integration_reference_crm"));

        ex.IsSequenceCollision().Should().BeFalse(
            "a unique violation elsewhere must surface as itself, not as a sequence retry");
    }

    [Fact]
    public void A_unique_violation_whose_constraint_name_is_absent_is_treated_as_the_sequence()
    {
        // Npgsql does not always populate ConstraintName. Treating that as a collision is the
        // conservative read on the write path this helper guards: the only unique index the
        // generator can violate is the sequence one, and a retry is harmless where a crash
        // abandons the cycle's file.
        var ex = new DbUpdateException("saving failed", Violation(constraintName: null));

        ex.IsSequenceCollision().Should().BeTrue();
    }

    [Fact]
    public void A_failure_that_is_not_a_unique_violation_is_not_a_collision()
    {
        var ex = new DbUpdateException(
            "saving failed", new InvalidOperationException("the column does not exist"));

        ex.IsSequenceCollision().Should().BeFalse();
    }

    [Fact]
    public void The_index_name_is_recognised_from_the_message_when_no_postgres_exception_surfaces()
    {
        // The branch that keeps this working under a provider that wraps nothing — the same
        // fallback KycLimitAlertUniqueViolation carries, and the reason the InMemory provider's
        // bare exceptions do not make the retry unreachable.
        var ex = new DbUpdateException(
            $"duplicate key value violates unique constraint \"{BatchSequenceAllocation.SequenceIndex}\"",
            new InvalidOperationException("no PostgresException here"));

        ex.IsSequenceCollision().Should().BeTrue();
    }

    /// <summary>
    /// A <see cref="PostgresException"/> shaped like the one Npgsql raises for SQLSTATE 23505.
    /// Constructed rather than provoked: provoking it needs a live PostgreSQL, and what is under
    /// test is the recognition, not the database.
    /// </summary>
    private static PostgresException Violation(string? constraintName)
        => new(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: "23505",
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: "integration",
            tableName: "integration_batch_file",
            columnName: null,
            dataTypeName: null,
            constraintName: constraintName);
}
