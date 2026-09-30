namespace Sankore.Modules.Leads.Tests.Features.Import;

using ClosedXML.Excel;
using FluentAssertions;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.CaptureLead.Events;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Modules.Leads.Features.Import.Readers;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Takes the first five rows of <c>docs/sample-lead-import.xlsx</c> and runs the real chain:
/// reader → parser → CaptureLeadHandler → LeadCapturedEvent → LeadAutoDispatchConsumer →
/// QualifyLeadHandler → DispatchLeadHandler, with the settings actually shipped in
/// appsettings.Development.json (AutoDispatchOnCapture = true, QualificationThreshold = 35).
///
/// What is faked: the agent pool (Administration is another module), the outbox publisher and
/// the bus. Everything that decides a score, a status or an agent is the production code.
/// </summary>
public sealed class ImportAutoDispatchEndToEndTests : IDisposable
{
    private const int RowsUnderTest = 5;
    private const int Threshold = 35;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly string _slicePath =
        Path.Combine(Path.GetTempPath(), $"slice-{Guid.NewGuid():N}.xlsx");

    public ImportAutoDispatchEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new TestDbContextFactory(_tenantId);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_slicePath)) File.Delete(_slicePath);
    }

    /// <summary>Header row plus the first five data rows of the committed sample file.</summary>
    private string BuildSlice()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SankoreCRM.sln")))
            dir = dir.Parent;

        var source = Path.Combine(dir!.FullName, "docs", "sample-lead-import.xlsx");
        File.Exists(source).Should().BeTrue(source);

        using var src = new XLWorkbook(source);
        var from = src.Worksheets.First();

        using var dst = new XLWorkbook();
        var to = dst.AddWorksheet("Leads");

        var lastColumn = from.LastColumnUsed()!.ColumnNumber();
        for (var r = 1; r <= RowsUnderTest + 1; r++)
            for (var c = 1; c <= lastColumn; c++)
                to.Cell(r, c).Value = from.Cell(r, c).Value;

        dst.SaveAs(_slicePath);
        return _slicePath;
    }

    private ServiceProvider BuildContainer(LeadsDbContext db, IAdministrationModule admin)
    {
        var services = new ServiceCollection();

        services.AddSingleton(db);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(admin);
        services.AddSingleton(Substitute.For<IBus>());
        services.AddSingleton(Substitute.For<IEventPublisher>());
        services.AddKeyedSingleton(nameof(LeadsDbContext), Substitute.For<IEventPublisher>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var indexer = Substitute.For<IPhoneBlindIndexer>();
        indexer.Compute(Arg.Any<string>()).Returns(ci => "idx-" + ci.Arg<string>());
        services.AddSingleton(indexer);

        services.AddSingleton(Options.Create(new LeadModuleSettings
        {
            AutoDispatchOnCapture  = true,
            QualificationThreshold = Threshold,
        }));

        services.AddSingleton<LeadScoreCalculator>();
        services.AddSingleton<DispatchingRuleResolver>();
        services.AddSingleton<CompatibilityScorer>();
        services.AddSingleton(new AgentCapacityService(db, null));
        services.AddSingleton(new DispatchingStrategyFactory(
            compatibilityScoring: new CompatibilityScoringStrategy(),
            roundRobin: new RoundRobinStrategy(),
            weightedRoundRobin: new WeightedRoundRobinStrategy(),
            stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
            cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy())));

        // Real handlers, no pipeline behaviors: validation, audit and transactions are the
        // bootstrapper's job and are exercised elsewhere.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(LeadsModule).Assembly));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Five_rows_are_captured_scored_and_dispatched()
    {
        await using var db = _factory.CreateContext();

        var agents = Enumerable.Range(1, 3)
            .Select(i => AgentTestBuilder.Create()
                .Named($"Agent {i}")
                .SpeakingLanguages("FR")
                .LocatedAt(5.33, -4.03)
                .WithLoad(i)
                .Build())
            .ToList();

        var admin = Substitute.For<IAdministrationModule>();
        admin.GetAvailableAgentsAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(agents);

        await using var sp = BuildContainer(db, admin);
        var sender = sp.GetRequiredService<ISender>();

        // ── 1. Read the five rows through the production reader ───────────
        var store = Substitute.For<IFileStore>();
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(File.OpenRead(BuildSlice())));

        var rows = await new FileImportReader(store).ReadAsync("slice.xlsx", CancellationToken.None);
        rows.Should().HaveCount(RowsUnderTest);

        var consumer = new LeadAutoDispatchConsumer(
            new FixedScopeFactory(sp),
            sp.GetRequiredService<IOptions<LeadModuleSettings>>(),
            NullLogger<LeadAutoDispatchConsumer>.Instance);

        var captured = 0;
        _output.WriteLine($"{"Lead",-22} {"score",5}  {"statut",-11} dispatch");
        _output.WriteLine(new string('-', 78));

        foreach (var raw in rows)
        {
            // ── 2. Parse and capture, exactly as ProcessLeadImportJob does ──
            var parsed = LeadRowParser.Parse(raw, new ImportDefaults(), LeadSource.FileImport);
            parsed.Errors.Should().BeEmpty();
            var row = parsed.Row!;

            var capture = await sender.Send(new CaptureLeadCommand(
                TenantId: _tenantId,
                FullName: row.FullName,
                PhoneNumber: row.PhoneNumber,
                Source: row.Source,
                InterestedProduct: row.InterestedProduct,
                PreferredLanguage: row.PreferredLanguage,
                Latitude: row.Latitude,
                Longitude: row.Longitude,
                PreferredAgencyId: row.AgencyId,   // exactly what ProcessLeadImportJob passes
                FirstName: row.FirstName,
                LastName: row.LastName,
                Email: row.Email,
                NationalId: row.NationalId,
                Gender: row.Gender,
                DateOfBirth: row.DateOfBirth,
                DesiredAmount: row.DesiredAmount,
                DesiredCurrency: row.DesiredCurrency,
                Campaign: row.Campaign,
                Channel: row.Channel,
                Comment: row.Comment,
                ExternalReference: row.ExternalReference,
                OwnerId: row.OwnerId,
                AgencyId: row.AgencyId,
                CompanyName: row.CompanyName));

            capture.IsSuccess.Should().BeTrue();
            var leadId = capture.Value.LeadId!.Value;
            captured++;

            // ── 3. What the outbox would deliver to the consumer ───────────
            await consumer.Consume(Delivered(new LeadCapturedEvent(
                LeadId: leadId,
                TenantId: _tenantId,
                Source: row.Source.ToString(),
                HasExplicitOwner: row.OwnerId.HasValue,
                DuplicateSuspected: capture.Value.DuplicateDetected)));

            var lead = await db.Leads.AsNoTracking().FirstAsync(l => l.Id == leadId);
            var assignment = await db.LeadAssignments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.LeadId == leadId);

            _output.WriteLine(
                $"{lead.FullName,-22} {lead.Score,5}  {lead.Status,-11} " +
                (assignment is null
                    ? "— non dispatché"
                    : $"→ agent {agents.First(a => a.Id == assignment.AgentId).FullName} " +
                      $"(compat {assignment.CompatibilityScore:F1}, règle {assignment.RuleId?.ToString() ?? "défaut"})"));
        }

        captured.Should().Be(RowsUnderTest);

        // ── 4. What the run produced ──────────────────────────────────────
        var leads = await db.Leads.AsNoTracking().ToListAsync();
        var assignments = await db.LeadAssignments.AsNoTracking().ToListAsync();
        var histories = await db.ScoreHistories.AsNoTracking().ToListAsync();

        leads.Should().HaveCount(RowsUnderTest);
        assignments.Should().HaveCount(RowsUnderTest,
            "with the gate relaxed every live lead is dispatched, whatever its score");

        leads.Should().OnlyContain(l => l.Score > 0, "every lead went through the calculator");
        leads.Should().OnlyContain(l => l.CurrentAssignmentId != null);
        leads.Should().OnlyContain(
            l => l.Status == LeadStatus.Qualified || l.Status == LeadStatus.Qualifying);

        // The threshold decides the label, not the dispatching: every lead is dispatched, and
        // the status is Qualified exactly when the score reaches the threshold.
        leads.Should().OnlyContain(
            l => (l.Score >= Threshold) == (l.Status == LeadStatus.Qualified));

        histories.Should().HaveCount(RowsUnderTest);
        histories.Should().OnlyContain(h => h.TriggerEvent == "AUTO_ON_CAPTURE");

        // Every assignment went to a real candidate, under the anti-monopoly ceiling. No spread
        // is asserted: the stubbed agents' counters never move between dispatches, so
        // CompatibilityScoring legitimately picks the same winner — that would test the stub.
        assignments.Should().OnlyContain(a => agents.Any(g => g.Id == a.AgentId));
        assignments.Should().OnlyContain(a => a.SlaDeadline > a.CreatedAt);

        _output.WriteLine(new string('-', 78));
        _output.WriteLine(
            $"{leads.Count} leads · {leads.Count(l => l.Status == LeadStatus.Qualified)} Qualified · " +
            $"{leads.Count(l => l.Status == LeadStatus.Qualifying)} Qualifying · " +
            $"{assignments.Count} dispatchés · score moyen {leads.Average(l => l.Score):F1}");
    }

    [Fact]
    public async Task A_tenant_with_no_dispatching_rule_still_gets_its_leads_dispatched()
    {
        // The requirement, made executable: configuring a DispatchingRule is optional. With an
        // empty table the resolver falls back to DispatchingRule.Default() — a complete rule, not
        // a null — so auto-dispatch works out of the box. Guards against a future change that
        // would make "no rule" mean "no dispatch".
        await using var db = _factory.CreateContext();

        (await db.DispatchingRules.CountAsync()).Should().Be(0, "nothing is seeded");

        var agent = AgentTestBuilder.Create().Named("Seule agente").SpeakingLanguages("FR").Build();
        var admin = Substitute.For<IAdministrationModule>();
        admin.GetAvailableAgentsAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { agent });

        await using var sp = BuildContainer(db, admin);
        var sender = sp.GetRequiredService<ISender>();

        var capture = await sender.Send(new CaptureLeadCommand(
            TenantId: _tenantId,
            FullName: "Awa Ouattara",
            PhoneNumber: "+2250708091801",
            Source: LeadSource.FileImport,
            InterestedProduct: "Crédit commerçant",
            PreferredLanguage: "FR",
            Latitude: 5.3,
            Longitude: -4.0,
            PreferredAgencyId: null));

        capture.IsSuccess.Should().BeTrue();
        var leadId = capture.Value.LeadId!.Value;

        var consumer = new LeadAutoDispatchConsumer(
            new FixedScopeFactory(sp),
            sp.GetRequiredService<IOptions<LeadModuleSettings>>(),
            NullLogger<LeadAutoDispatchConsumer>.Instance);

        await consumer.Consume(Delivered(new LeadCapturedEvent(
            leadId, _tenantId, nameof(LeadSource.FileImport), false, false)));

        var assignment = await db.LeadAssignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.LeadId == leadId);

        assignment.Should().NotBeNull("no configured rule must not mean no dispatching");
        assignment!.AgentId.Should().Be(agent.Id);

        // Default()'s own values were the ones applied.
        assignment.Strategy.Should().Be(DispatchingStrategy.CompatibilityScoring);
        assignment.RuleId.Should().BeNull("Default() is not a persisted rule");
        (assignment.SlaDeadline - assignment.CreatedAt).Should()
            .BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromSeconds(5),
                "DispatchingRule.Default().FirstContactSla");

        var lead = await db.Leads.AsNoTracking().FirstAsync(l => l.Id == leadId);
        lead.CurrentAssignedId.Should().Be(agent.Id);
    }

    [Fact]
    public async Task Nothing_is_dispatched_when_the_setting_is_off()
    {
        await using var db = _factory.CreateContext();
        var admin = Substitute.For<IAdministrationModule>();
        await using var sp = BuildContainer(db, admin);

        var consumer = new LeadAutoDispatchConsumer(
            new FixedScopeFactory(sp),
            Options.Create(new LeadModuleSettings { AutoDispatchOnCapture = false }),
            NullLogger<LeadAutoDispatchConsumer>.Instance);

        await consumer.Consume(Delivered(new LeadCapturedEvent(
            Guid.NewGuid(), _tenantId, "FileImport", false, false)));

        await admin.DidNotReceive().GetAvailableAgentsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>What MassTransit would hand the consumer once the outbox published the event.</summary>
    private static ConsumeContext<LeadCapturedEvent> Delivered(LeadCapturedEvent evt)
    {
        var context = Substitute.For<ConsumeContext<LeadCapturedEvent>>();
        context.Message.Returns(evt);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    /// <summary>The consumer creates its own DI scope; here every scope is the same container.</summary>
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
