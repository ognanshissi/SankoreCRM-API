namespace Sankore.Shared.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Makes every <see cref="DateTimeOffset"/> column store UTC, whatever offset the value arrived
/// with.
///
/// <para>
/// Npgsql maps <see cref="DateTimeOffset"/> to <c>timestamp with time zone</c> and <b>refuses any
/// offset but zero</b>:
/// <c>Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type 'timestamp with time
/// zone', only offset 0 (UTC) is supported.</c> It is an <see cref="ArgumentException"/> thrown
/// inside <c>SaveChanges</c>, so it surfaces as a 500 on an ordinary write with nothing in the
/// message naming the property or the request.
/// </para>
///
/// <para>
/// <b>Why a convention and not a fix at each call site.</b> Nothing in this solution produces a
/// local timestamp — there is no <c>DateTimeOffset.Now</c> anywhere, only <c>UtcNow</c> and
/// <c>TimeProvider.GetUtcNow()</c>. The offset comes from OUTSIDE: a request body carrying
/// <c>2026-10-10T14:00:00+02:00</c>, bound straight onto a command and written to a column. Every
/// endpoint taking a client-supplied instant is one of these — an activity's <c>ScheduledAt</c>, a
/// first contact's <c>ContactedAt</c>, a secret's <c>ExpiresAt</c>, a relay agent's
/// <c>CheckedAt</c>, a task's <c>DueAt</c> — and the next one added would be too. Converting on
/// the way to the column puts "this database stores UTC" where it is true for every write path:
/// HTTP, Hangfire jobs, MassTransit consumers and seeders alike.
/// </para>
///
/// <para>
/// Lossless for ordering and comparison: <see cref="DateTimeOffset.ToUniversalTime"/> keeps the
/// same instant. What it drops is the originating offset, which no column here stores anyway —
/// a <c>timestamptz</c> is a point in time, not a wall clock. Reading back is a no-op: Npgsql
/// already hands back offset 0.
/// </para>
/// </summary>
public static class UtcTimestamps
{
    /// <summary>
    /// Call from a DbContext's <c>ConfigureConventions</c>. Applies to
    /// <see cref="DateTimeOffset"/> and <c>DateTimeOffset?</c> alike — EF applies a value
    /// converter registered for a type to its nullable form as well.
    /// </summary>
    public static void UseUtcDateTimeOffsets(this ModelConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}

/// <summary>
/// Normalises on the way to the column and leaves the way back alone — Npgsql already returns
/// offset 0. A named class rather than an inline lambda because
/// <c>ModelConfigurationBuilder.HaveConversion</c> takes a converter TYPE, not an instance.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetConverter()
        : base(write => write.ToUniversalTime(), read => read) { }
}
