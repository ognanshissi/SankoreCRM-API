namespace Sankore.Modules.Leads.Tests.Persistence;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Npgsql refuses a <see cref="DateTimeOffset"/> whose offset is not zero:
/// <c>Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type 'timestamp with time
/// zone', only offset 0 (UTC) is supported.</c> It throws inside <c>SaveChanges</c>, so an
/// ordinary write becomes a 500.
///
/// <para>
/// Nothing in the solution produces a local timestamp — the offset arrives from a client, in a
/// request body such as <c>{"scheduledAt":"2026-10-10T14:00:00+02:00"}</c>, bound onto a command
/// and written straight to a column. <c>UtcTimestamps.UseUtcDateTimeOffsets</c> converts on the
/// way down from every DbContext's <c>ConfigureConventions</c>.
/// </para>
///
/// <para>
/// <b>These assertions are about the converter, not about Npgsql.</b> The suite's InMemory
/// provider accepts any offset happily, so it can never reproduce the exception itself — but it
/// does run value converters, which is the half that has to hold.
/// </para>
/// </summary>
public sealed class UtcTimestampConventionTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public UtcTimestampConventionTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    /// <summary>14:00 in Paris in summer — the shape a browser sends.</summary>
    private static readonly DateTimeOffset ParisAfternoon =
        new(2026, 10, 10, 14, 0, 0, TimeSpan.FromHours(2));

    private Lead NewLead() => Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: LeadSource.Agency,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System,
        firstName: "Aya",
        lastName: "Kouassi",
        ownerId: Guid.NewGuid());

    [Fact]
    public async Task A_client_supplied_offset_is_stored_as_the_same_instant_in_utc()
    {
        var lead = NewLead();

        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            seed.LeadActivities.Add(LeadActivity.Create(
                tenantId:    _tenant,
                leadId:      lead.Id,
                type:        ActivityType.Call,
                subject:     "Rappeler le prospect",
                performedBy: Guid.NewGuid(),
                scheduledAt: ParisAfternoon));

            await seed.SaveChangesAsync();
        }

        await using var verify = _factory.CreateContext();
        var stored = await verify.LeadActivities.SingleAsync(a => a.LeadId == lead.Id);

        stored.ScheduledAt.Should().NotBeNull();
        stored.ScheduledAt!.Value.Offset.Should().Be(TimeSpan.Zero,
            "Npgsql writes nothing else to a timestamptz column");

        // The same point in time, not the same wall clock: 14:00+02:00 is 12:00Z.
        stored.ScheduledAt.Value.Should().Be(ParisAfternoon);
        stored.ScheduledAt.Value.Hour.Should().Be(12);
    }

    [Fact]
    public async Task A_value_already_in_utc_is_untouched()
    {
        var lead = NewLead();
        var utc = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            seed.LeadActivities.Add(LeadActivity.Create(
                tenantId:    _tenant,
                leadId:      lead.Id,
                type:        ActivityType.Call,
                subject:     "Rappeler le prospect",
                performedBy: Guid.NewGuid(),
                scheduledAt: utc));

            await seed.SaveChangesAsync();
        }

        await using var verify = _factory.CreateContext();
        var stored = await verify.LeadActivities.SingleAsync(a => a.LeadId == lead.Id);

        stored.ScheduledAt.Should().Be(utc);
    }

    [Fact]
    public void Every_DateTimeOffset_property_in_the_model_carries_the_conversion()
    {
        using var db = _factory.CreateContext();

        var unconverted = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?))
            .Where(p => p.GetValueConverter() is null)
            .Select(p => $"{p.DeclaringType.ShortName()}.{p.Name}")
            .OrderBy(n => n)
            .ToList();

        // A convention, so a column added later is covered without anybody remembering. If this
        // ever fails, the DbContext lost its ConfigureConventions override.
        unconverted.Should().BeEmpty();
    }
}
