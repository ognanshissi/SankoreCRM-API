namespace Sankore.Modules.Leads.Tests.Features.Nurturing;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.NurtureLead;
using Sankore.Modules.Leads.Features.NurturingExecution;
using Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;
using Sankore.Modules.Leads.Features.NurturingSequences.GetNurturingSequence;
using Sankore.Modules.Leads.Features.NurturingSequences.UpdateNurturingSequence;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// What the sequence screens and the « Placer en nurturing » action rely on: steps can be changed,
/// a lead always gets a sequence, and a commercial e-mail only leaves for someone still in nurturing,
/// still consenting, and with a template that exists.
/// </summary>
public sealed class NurturingTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();

    public NurturingTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _notifications.HasActiveEmailTemplateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _notifications.QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));
    }

    public void Dispose() => _factory.Dispose();

    private static StepInput Step(int order, int days, string key = "nurture.a") => new(order, TimeSpan.FromDays(days), key);

    private async Task<Guid> SeedSequenceAsync(params StepInput[] steps)
    {
        await using var db = _factory.CreateContext();
        var result = await new CreateNurturingSequenceHandler(db, _notifications).Handle(
            new CreateNurturingSequenceCommand(_tenant, "Relance", null, steps.Length > 0 ? steps : [Step(0, 0)]), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    private async Task<Guid> SeedLeadAsync(bool consent = true, string? email = "awa@example.test", bool nurturing = false, string language = "fr")
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).WithLanguage(language).Build();
        if (email is not null) lead.ApplyMergeOverrides(email: email, emailBlindIndex: "index");
        if (nurturing) lead.Nurture();
        db.Leads.Add(lead);
        if (consent)
            db.LeadConsents.Add(LeadConsent.Create(_tenant, lead.Id, ConsentType.Marketing, ConsentChannel.WebForm, Guid.NewGuid(), TimeProvider.System));
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private async Task<Result> NurtureAsync(Guid leadId, Guid? sequenceId)
    {
        await using var db = _factory.CreateContext();
        return await new NurtureLeadHandler(db, TimeProvider.System).Handle(new NurtureLeadCommand(leadId, sequenceId), CancellationToken.None);
    }

    private async Task<NurturingEnrollment> EnrollmentAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await db.NurturingEnrollments.SingleAsync(e => e.LeadId == leadId);
    }

    private async Task RunJobAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<LeadsDbContext>(_ => _factory.CreateContext());
        services.AddSingleton(_notifications);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await new ExecuteNurturingJob(provider.GetRequiredService<IServiceScopeFactory>()).ExecuteAsync(_tenant);
    }

    private Task SentNothing() => _notifications.DidNotReceive().QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>());

    // ——— Sequences ———

    [Fact]
    public async Task Updating_a_sequence_replaces_its_steps()
    {
        var id = await SeedSequenceAsync(Step(0, 0, "nurture.a"), Step(1, 7, "nurture.b"));

        await using (var db = _factory.CreateContext())
            (await new UpdateNurturingSequenceHandler(db, _notifications).Handle(
                new UpdateNurturingSequenceCommand(id, "Renommée", "Texte", [Step(0, 2, "nurture.c"), Step(1, 3, "nurture.d"), Step(2, 4, "nurture.e")]),
                CancellationToken.None)).IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var dto = (await new GetNurturingSequenceHandler(read).Handle(new GetNurturingSequenceQuery(id), CancellationToken.None)).Value!;
        dto.Name.Should().Be("Renommée");
        dto.Steps.Select(s => s.EmailTemplateKey).Should().Equal("nurture.c", "nurture.d", "nurture.e");
        dto.Steps.Select(s => s.Order).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task A_step_naming_a_template_that_does_not_exist_is_refused_at_creation_and_update()
    {
        _notifications.HasActiveEmailTemplateAsync(_tenant, "inconnu", "fr", Arg.Any<CancellationToken>()).Returns(false);
        var id = await SeedSequenceAsync();

        await using var db = _factory.CreateContext();
        (await new CreateNurturingSequenceHandler(db, _notifications).Handle(
            new CreateNurturingSequenceCommand(_tenant, "X", null, [Step(0, 0, "inconnu")]), CancellationToken.None))
            .Error.Should().Be("NURTURING_TEMPLATE_NOT_FOUND");
        (await new UpdateNurturingSequenceHandler(db, _notifications).Handle(
            new UpdateNurturingSequenceCommand(id, "X", null, [Step(0, 0, "inconnu")]), CancellationToken.None))
            .Error.Should().Be("NURTURING_TEMPLATE_NOT_FOUND");
    }

    [Fact]
    public async Task A_sequence_says_how_many_leads_are_on_it()
    {
        var id = await SeedSequenceAsync(Step(0, 3));
        (await NurtureAsync(await SeedLeadAsync(), id)).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        (await new GetNurturingSequenceHandler(db).Handle(new GetNurturingSequenceQuery(id), CancellationToken.None))
            .Value!.ActiveEnrollments.Should().Be(1);
    }

    [Fact]
    public async Task A_lead_that_left_nurturing_is_no_longer_counted_on_its_sequence()
    {
        var id = await SeedSequenceAsync(Step(0, 10));
        var lead = await SeedLeadAsync();
        await NurtureAsync(lead, id);

        await using (var db = _factory.CreateContext())
        {
            (await db.Leads.AsTracking().SingleAsync(l => l.Id == lead)).Recycle();
            await db.SaveChangesAsync();
        }

        await using var read = _factory.CreateContext();
        (await new GetNurturingSequenceHandler(read).Handle(new GetNurturingSequenceQuery(id), CancellationToken.None))
            .Value!.ActiveEnrollments.Should().Be(0);
    }

    // ——— Placing a lead ———

    [Fact]
    public async Task Placing_a_lead_needs_a_sequence()
        => (await NurtureAsync(await SeedLeadAsync(), null)).Error.Should().Be("SEQUENCE_REQUIRED");

    [Fact]
    public async Task Placing_a_lead_needs_its_marketing_consent()
        => (await NurtureAsync(await SeedLeadAsync(consent: false), await SeedSequenceAsync()))
            .Error.Should().Be("MARKETING_CONSENT_REQUIRED");

    [Fact]
    public async Task Placing_a_lead_needs_an_email_address()
        => (await NurtureAsync(await SeedLeadAsync(email: null), await SeedSequenceAsync()))
            .Error.Should().Be("LEAD_HAS_NO_EMAIL");

    [Fact]
    public async Task A_lead_already_in_nurturing_without_a_sequence_can_get_one_but_not_a_second()
    {
        var lead = await SeedLeadAsync(nurturing: true);
        var sequence = await SeedSequenceAsync();

        (await NurtureAsync(lead, sequence)).IsSuccess.Should().BeTrue();
        (await EnrollmentAsync(lead)).Status.Should().Be(NurturingEnrollmentStatus.Active);

        (await NurtureAsync(lead, sequence)).Error.Should().Be("LEAD_ALREADY_ENROLLED");
    }

    [Fact]
    public async Task A_lead_coming_back_to_nurturing_starts_a_new_round_and_the_old_one_stops()
    {
        var lead = await SeedLeadAsync();
        var sequence = await SeedSequenceAsync(Step(0, 30));
        await NurtureAsync(lead, sequence);

        await using (var db = _factory.CreateContext())
        {
            var entity = await db.Leads.AsTracking().SingleAsync(l => l.Id == lead);
            entity.Close(LeadCloseReason.Lost, "Plus intéressé");
            entity.Reopen();
            await db.SaveChangesAsync();
        }

        (await NurtureAsync(lead, sequence)).IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var rounds = await read.NurturingEnrollments.Where(e => e.LeadId == lead).ToListAsync();
        rounds.Count(e => e.Status == NurturingEnrollmentStatus.Active).Should().Be(1);
        rounds.Count(e => e.Status == NurturingEnrollmentStatus.Cancelled).Should().Be(1);
    }

    // ——— Sending ———

    [Fact]
    public async Task A_due_step_is_sent_with_the_name_the_template_can_use()
    {
        var lead = await SeedLeadAsync();
        await NurtureAsync(lead, await SeedSequenceAsync(Step(0, 0), Step(1, 7)));

        await RunJobAsync();

        await _notifications.Received(1).QueueEmailAsync(
            Arg.Is<QueueEmailRequest>(r => r.TemplateKey == "nurture.a"
                && r.RecipientEmail == "awa@example.test"
                && r.TemplateData.ContainsKey("first_name")
                && r.TemplateData.ContainsKey("lead_name")),
            Arg.Any<CancellationToken>());
        (await EnrollmentAsync(lead)).LastCompletedStepIndex.Should().Be(0);
    }

    [Fact]
    public async Task A_lead_that_left_nurturing_receives_nothing_and_its_round_stops()
    {
        var lead = await SeedLeadAsync();
        await NurtureAsync(lead, await SeedSequenceAsync());
        await using (var db = _factory.CreateContext())
        {
            (await db.Leads.AsTracking().SingleAsync(l => l.Id == lead)).Close(LeadCloseReason.Lost, "Plus intéressé");
            await db.SaveChangesAsync();
        }

        await RunJobAsync();

        await SentNothing();
        var round = await EnrollmentAsync(lead);
        round.Status.Should().Be(NurturingEnrollmentStatus.Cancelled);
        round.CancellationReason.Should().Be("LEAD_LEFT_NURTURING");
    }

    [Fact]
    public async Task A_consent_that_is_no_longer_active_stops_the_round_without_sending()
    {
        var lead = await SeedLeadAsync();
        await NurtureAsync(lead, await SeedSequenceAsync());
        await using (var db = _factory.CreateContext())
        {
            (await db.LeadConsents.AsTracking().SingleAsync(c => c.LeadId == lead)).Withdraw(Guid.NewGuid(), TimeProvider.System, "Retrait");
            await db.SaveChangesAsync();
        }

        await RunJobAsync();

        await SentNothing();
        (await EnrollmentAsync(lead)).Status.Should().Be(NurturingEnrollmentStatus.Cancelled);
    }

    [Fact]
    public async Task With_no_template_at_all_the_step_waits_rather_than_mailing_raw_json()
    {
        var lead = await SeedLeadAsync();
        await NurtureAsync(lead, await SeedSequenceAsync());
        _notifications.HasActiveEmailTemplateAsync(_tenant, "nurture.a", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await RunJobAsync();

        await SentNothing();
        var round = await EnrollmentAsync(lead);
        round.Status.Should().Be(NurturingEnrollmentStatus.Active);
        round.LastCompletedStepIndex.Should().Be(-1);
    }

    [Fact]
    public async Task A_template_missing_in_the_leads_language_falls_back_on_french()
    {
        var lead = await SeedLeadAsync(language: "en");
        await NurtureAsync(lead, await SeedSequenceAsync());
        _notifications.HasActiveEmailTemplateAsync(_tenant, "nurture.a", "en", Arg.Any<CancellationToken>()).Returns(false);

        await RunJobAsync();

        await _notifications.Received(1).QueueEmailAsync(Arg.Is<QueueEmailRequest>(r => r.Locale == "fr"), Arg.Any<CancellationToken>());
    }
}
