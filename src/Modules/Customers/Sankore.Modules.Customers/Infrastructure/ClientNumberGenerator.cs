namespace Sankore.Modules.Customers.Infrastructure;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Allocates the human-readable client number ("BOU-2026-000042").
/// </summary>
public interface IClientNumberGenerator
{
    Task<string> NextAsync(Guid tenantId, string agencyCode, CancellationToken ct);
}

/// <summary>
/// Renders the tenant's <c>client-number-format</c> against a per-tenant/agency/year
/// counter held in <c>customers.client_number_sequences</c>.
///
/// Supported tokens: <c>{AgencyCode}</c>, <c>{YYYY}</c>, <c>{YY}</c>, <c>{Seq}</c> and
/// <c>{Seq:N}</c> where N is the zero-pad width (the default format uses <c>{Seq:6}</c>).
///
/// Numbers are never reused. The counter only ever moves forward: a failed client
/// creation burns its number rather than handing it to the next client, because a
/// reused number would make two different people indistinguishable in the paper trail
/// and in the core banking system.
///
/// Concurrency is handled with the sequence row's xmin token instead of a lock: two
/// simultaneous allocations for the same agency both read the same counter, one commits,
/// the other gets a <see cref="DbUpdateConcurrencyException"/> and retries on a fresh
/// read. The "create the row when absent" path is protected the same way by the unique
/// index on (tenant, agency_code, year), which surfaces as a unique-violation
/// <see cref="DbUpdateException"/>.
///
/// IMPORTANT: this type calls <c>SaveChangesAsync</c> itself so the counter is durable
/// before the caller builds the client. It therefore enlists in the ambient
/// <c>TransactionScope</c> opened by <c>TransactionBehavior</c>: if the surrounding
/// command later fails, the counter increment rolls back with it — which is safe, since
/// the only consequence is that the number is handed out again to the next caller rather
/// than being skipped.
/// </summary>
internal sealed class ClientNumberGenerator(CustomersDbContext db, ICustomerSettings settings)
    : IClientNumberGenerator
{
    /// <summary>Retries on contention. Five attempts is far beyond what real traffic needs.</summary>
    private const int MaxAttempts = 5;

    private static readonly Regex SeqToken = new(
        @"\{Seq(?::(?<width>\d{1,2}))?\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public async Task<string> NextAsync(Guid tenantId, string agencyCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agencyCode);

        var format = await settings.GetStringAsync(tenantId, "client-number-format", ct);
        var year = DateTimeOffset.UtcNow.Year;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var next = await TakeNextAsync(tenantId, agencyCode, year, ct);
                return Render(format, agencyCode, year, next);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Another allocation won the race on this counter row.
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && IsUniqueViolation(ex))
            {
                // Another allocation created the counter row first; read it and take a value from it.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<int> TakeNextAsync(Guid tenantId, string agencyCode, int year, CancellationToken ct)
    {
        // AsTracking because the row is mutated; IgnoreQueryFilters + explicit predicate
        // because the caller may be a background job with no ambient tenant.
        var sequence = await db.ClientNumberSequences
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.AgencyCode == agencyCode && s.Year == year, ct);

        if (sequence is null)
        {
            sequence = ClientNumberSequence.Start(tenantId, agencyCode, year);
            db.ClientNumberSequences.Add(sequence);
        }

        var value = sequence.Take();
        await db.SaveChangesAsync(ct);
        return value;
    }

    private static string Render(string format, string agencyCode, int year, int sequence)
    {
        var rendered = format
            .Replace("{AgencyCode}", agencyCode, StringComparison.OrdinalIgnoreCase)
            .Replace("{YYYY}", year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{YY}", (year % 100).ToString("D2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        return SeqToken.Replace(rendered, match =>
        {
            var width = match.Groups["width"].Success
                ? int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture)
                : 1;
            return sequence.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        });
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
