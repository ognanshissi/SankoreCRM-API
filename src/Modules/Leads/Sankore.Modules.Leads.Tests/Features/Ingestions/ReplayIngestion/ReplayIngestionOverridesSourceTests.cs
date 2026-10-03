namespace Sankore.Modules.Leads.Tests.Features.Ingestions.ReplayIngestion;

using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.Ingestions.ReplayIngestion;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A security property, not a convenience.
///
/// ReplayIngestionJob deserializes <c>LeadIngestion.RawPayloadJson</c> straight into a
/// <c>CaptureLeadCommand</c>, and for a web or webhook source that payload is whatever the
/// remote caller posted. Since <c>CaptureLeadCommand</c> now carries a LeadSourceConfigId, a
/// payload could claim one — and a claimed source would hand its author that source's
/// dispatching rule (so its agent pool) and bill its cost-per-lead against it. The ingestion row
/// is the only trustworthy record of where the payload actually came from, so the job overrides
/// the field unconditionally rather than defaulting it.
/// </summary>
public sealed class ReplayIngestionOverridesSourceTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public ReplayIngestionOverridesSourceTests()
        => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_ingestion_row_wins_over_a_source_claimed_in_the_payload()
    {
        var realSourceId    = Guid.NewGuid();   // where the payload actually arrived
        var claimedSourceId = Guid.NewGuid();   // what the payload says about itself

        var payload = JsonSerializer.Serialize(new CaptureLeadCommand(
            TenantId: _tenantId,
            FullName: "Seydou Bamba",
            PhoneNumber: "+2250701020304",
            Source: LeadSource.Web,
            InterestedProduct: "Crédit",
            PreferredLanguage: "fr",
            Latitude: 5.33,
            Longitude: -4.03,
            PreferredAgencyId: null,
            LeadSourceConfigId: claimedSourceId));

        await using var seed = _factory.CreateContext();
        var ingestion = LeadIngestion.Create(
            _tenantId, Guid.NewGuid(), realSourceId, TimeProvider.System,
            runId: null, rawPayloadJson: payload, externalId: "ext-1");
        ingestion.Reject("to be replayed");
        seed.LeadIngestions.Add(ingestion);
        await seed.SaveChangesAsync();

        CaptureLeadCommand? sent = null;
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Do<CaptureLeadCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new CaptureLeadResult(Guid.NewGuid(), "New")));

        await using var db = _factory.CreateContext();
        await using var sp = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(sender)
            .AddSingleton<ILogger<ReplayIngestionJob>>(NullLogger<ReplayIngestionJob>.Instance)
            .BuildServiceProvider();

        await new ReplayIngestionJob(new FixedScopeFactory(sp))
            .ExecuteAsync(ingestion.Id, _tenantId);

        // The job swallows every exception, so a broken arrange would otherwise read as a pass.
        sent.Should().NotBeNull("the job must have dispatched a command");
        sent!.LeadSourceConfigId.Should().Be(
            realSourceId,
            "the ingestion row is the only trustworthy statement of the lead's origin");
        sent.LeadSourceConfigId.Should().NotBe(
            claimedSourceId,
            "a replayed payload must not be able to name its own source");
    }

    /// <summary>The job creates its own DI scope; here every scope is the same container.</summary>
    private sealed class FixedScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
