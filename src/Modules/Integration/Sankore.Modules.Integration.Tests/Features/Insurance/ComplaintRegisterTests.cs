namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Complaints;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// The <i>mécanisme de traitement des réclamations</i> of the règlement
/// n° 01/CIMA/PCMA/CE/SG/2024.
///
/// <para>
/// The cases worth reading first are
/// <see cref="The_deadlines_run_from_when_the_CUSTOMER_complained"/> — a letter logged three days
/// late must not buy the institution three extra days — <see cref="Acknowledging_twice_keeps_the_FIRST_instant"/>,
/// without which a late acknowledgement could be made to look punctual by sending a second one,
/// and <see cref="A_breach_counts_whether_the_step_was_late_OR_still_undone"/>: a report that only
/// counted late steps would improve by never acknowledging at all.
/// </para>
/// </summary>
public sealed class ComplaintRegisterTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<Result<ComplaintDto>> RecordAsync(
        ComplaintCategory category = ComplaintCategory.Premium,
        ComplaintChannel channel = ComplaintChannel.Branch,
        string subject = "Prime prélevée deux fois en mai",
        string? body = "Le client indique deux prélèvements de 7 500 F le même jour.",
        DateTimeOffset? receivedAt = null,
        Guid? claimId = null,
        int acknowledgementDays = 5,
        int resolutionDays = 30)
    {
        await using var db = _factory.CreateContext();

        return await new RecordComplaintHandler(
                db,
                Features.Commands.CommandsTestHarness.Encryptor(),
                User(Tenant),
                Options.Create(new ComplaintOptions
                {
                    AcknowledgementDays = acknowledgementDays,
                    ResolutionDays = resolutionDays,
                }),
                Clock(Now),
                NullLogger<RecordComplaintHandler>.Instance)
            .Handle(
                new RecordComplaintCommand(
                    CrmCustomerId: Customer,
                    Channel: channel,
                    Category: category,
                    Subject: subject,
                    Body: body,
                    ConnectionId: null,
                    PolicyId: null,
                    ClaimId: claimId,
                    ReceivedAt: receivedAt),
                CancellationToken.None);
    }

    private async Task<Result<ComplaintDto>> AdvanceAsync(
        Guid complaintId, ComplaintAction action, string? text = null, bool wasUpheld = false,
        DateTimeOffset? at = null)
    {
        await using var db = _factory.CreateContext();

        return await new AdvanceComplaintHandler(
                db, User(Tenant), Clock(at ?? Now),
                NullLogger<AdvanceComplaintHandler>.Instance)
            .Handle(
                new AdvanceComplaintCommand(complaintId, action, text, wasUpheld),
                CancellationToken.None);
    }

    private async Task<InsuranceComplaint> ComplaintAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.InsuranceComplaints.IgnoreQueryFilters().FirstAsync(c => c.Id == id);
    }

    // ── recording ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_complaint_gets_a_reference_the_customer_can_quote_back()
    {
        var result = await RecordAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Reference.Should().Be("REC-2026-000001");
        result.Value.Status.Should().Be(nameof(ComplaintStatus.Received));
        result.Value.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task References_run_per_tenant_and_per_YEAR()
    {
        // Read-then-insert; ux_ins_complaint_reference is what makes it true under concurrency,
        // and EF InMemory honours no unique index — so this pins the read half.
        await RecordAsync();
        await RecordAsync();
        var third = await RecordAsync();

        third.Value!.Reference.Should().Be("REC-2026-000003");

        // A complaint received in a previous year starts that year's own sequence.
        var old = await RecordAsync(receivedAt: new DateTimeOffset(2025, 11, 2, 8, 0, 0, TimeSpan.Zero));

        old.Value!.Reference.Should().Be("REC-2025-000001");
    }

    [Fact]
    public async Task The_deadlines_run_from_when_the_CUSTOMER_complained()
    {
        // A letter logged three days late must not buy the institution three extra days.
        var complained = Now.AddDays(-3);

        var result = await RecordAsync(
            receivedAt: complained, acknowledgementDays: 5, resolutionDays: 30);

        result.Value!.AcknowledgementDueAt.Should().Be(complained.AddDays(5));
        result.Value.ResolutionDueAt.Should().Be(complained.AddDays(30));
    }

    [Fact]
    public async Task A_complaint_received_in_the_FUTURE_is_refused()
    {
        // That is a typo, and honouring it would push both deadlines out.
        var result = await RecordAsync(receivedAt: Now.AddDays(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("future");
    }

    [Fact]
    public async Task What_the_customer_said_is_encrypted_and_the_subject_is_not()
    {
        var result = await RecordAsync(
            subject: "Prime prélevée deux fois",
            body: "Mon compte a été débité deux fois le 5 mai.");

        var complaint = await ComplaintAsync(result.Value!.Id);

        complaint.BodyEncrypted.Should().StartWith("v1:");
        complaint.BodyEncrypted.Should().NotContain("débité");

        // In clear, because it appears in lists and in a supervisor's report.
        complaint.Subject.Should().Be("Prime prélevée deux fois");
    }

    [Fact]
    public async Task An_absent_body_stays_null_rather_than_a_decryptable_blank()
    {
        var result = await RecordAsync(body: null);

        (await ComplaintAsync(result.Value!.Id)).BodyEncrypted.Should().BeNull();
        result.Value.HasBody.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(400, 5)]
    [InlineData(10, 10)]
    public void An_absurd_configured_deadline_falls_back(int configured, int expected)
    {
        // A deadline of zero days would make every complaint overdue the moment it is recorded,
        // and a typo in a configuration file is exactly how that would arrive.
        new ComplaintOptions { AcknowledgementDays = configured }
            .ResolvedAcknowledgementDays.Should().Be(expected);
    }

    [Fact]
    public void An_answer_deadline_shorter_than_the_acknowledgement_s_is_corrected()
    {
        // The aggregate refuses the pair outright, so the options must never hand it one.
        var options = new ComplaintOptions { AcknowledgementDays = 20, ResolutionDays = 5 };

        options.ResolvedResolutionDays.Should()
            .BeGreaterThanOrEqualTo(options.ResolvedAcknowledgementDays);
    }

    // ── acknowledging ───────────────────────────────────────────────────────

    [Fact]
    public async Task Acknowledging_moves_the_status_and_stamps_the_actor()
    {
        var recorded = await RecordAsync();

        var result = await AdvanceAsync(recorded.Value!.Id, ComplaintAction.Acknowledge);

        result.IsSuccess.Should().BeTrue();

        var complaint = await ComplaintAsync(recorded.Value.Id);
        complaint.Status.Should().Be(ComplaintStatus.Acknowledged);
        complaint.AcknowledgedAt.Should().Be(Now);
        complaint.AcknowledgedBy.Should().Be(Actor);
    }

    [Fact]
    public async Task Acknowledging_twice_keeps_the_FIRST_instant()
    {
        // Re-stamping would let a late acknowledgement be made to look punctual by sending a
        // second one.
        var recorded = await RecordAsync();

        await AdvanceAsync(recorded.Value!.Id, ComplaintAction.Acknowledge);
        await AdvanceAsync(recorded.Value.Id, ComplaintAction.Acknowledge, at: Now.AddDays(10));

        (await ComplaintAsync(recorded.Value.Id)).AcknowledgedAt.Should().Be(Now);
    }

    // ── escalating ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Escalation_is_NOT_terminal_and_the_answer_is_still_owed()
    {
        // The institution still owes the customer an answer and the resolution deadline keeps
        // running, which is why escalation is a status and not a closure.
        var recorded = await RecordAsync();

        await AdvanceAsync(
            recorded.Value!.Id, ComplaintAction.Escalate,
            text: "Direction Nationale des Assurances — Côte d'Ivoire");

        var complaint = await ComplaintAsync(recorded.Value.Id);
        complaint.Status.Should().Be(ComplaintStatus.Escalated);
        complaint.IsOpen.Should().BeTrue();
        complaint.EscalatedTo.Should().Be("Direction Nationale des Assurances — Côte d'Ivoire");
    }

    [Fact]
    public async Task Acknowledging_an_ESCALATED_complaint_does_not_bring_it_back()
    {
        // A complaint handed to the supervisor and only then acknowledged to the customer has not
        // come back to us.
        var recorded = await RecordAsync();

        await AdvanceAsync(recorded.Value!.Id, ComplaintAction.Escalate, text: "Assureur");
        await AdvanceAsync(recorded.Value.Id, ComplaintAction.Acknowledge);

        var complaint = await ComplaintAsync(recorded.Value.Id);
        complaint.Status.Should().Be(ComplaintStatus.Escalated);
        complaint.AcknowledgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_escalation_with_no_target_is_refused()
    {
        var recorded = await RecordAsync();

        var result = await AdvanceAsync(
            recorded.Value!.Id, ComplaintAction.Escalate, text: "   ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ComplaintErrors.EscalationTargetRequired);
    }

    // ── answering ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_answer_with_no_text_is_refused_even_for_a_REJECTION()
    {
        // "We considered your complaint and rejected it" with no reason is exactly the answer this
        // mechanism exists to prevent, and it is the one a supervisor asks to see.
        var recorded = await RecordAsync();

        var result = await AdvanceAsync(
            recorded.Value!.Id, ComplaintAction.Resolve, text: null, wasUpheld: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ComplaintErrors.ResolutionRequired);
    }

    [Fact]
    public async Task Answering_records_the_text_in_clear_and_whether_it_was_upheld()
    {
        var recorded = await RecordAsync();

        await AdvanceAsync(
            recorded.Value!.Id, ComplaintAction.Resolve,
            text: "Double prélèvement confirmé, 7 500 F recrédités le 10 juin.",
            wasUpheld: true);

        var complaint = await ComplaintAsync(recorded.Value.Id);
        complaint.Status.Should().Be(ComplaintStatus.Resolved);
        complaint.IsOpen.Should().BeFalse();
        complaint.WasUpheld.Should().BeTrue();

        // In clear: a controller asking what was answered must not be handed a ciphertext.
        complaint.Resolution.Should().StartWith("Double prélèvement confirmé");
        complaint.ClosedBy.Should().Be(Actor);
    }

    [Fact]
    public async Task Re_answering_an_answered_complaint_is_refused()
    {
        // Re-answering would rewrite what the customer was told.
        var recorded = await RecordAsync();

        await AdvanceAsync(recorded.Value!.Id, ComplaintAction.Resolve, text: "Remboursé.", wasUpheld: true);
        var second = await AdvanceAsync(
            recorded.Value.Id, ComplaintAction.Resolve, text: "En fait non.", wasUpheld: false);

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(ComplaintErrors.AlreadyClosed);

        (await ComplaintAsync(recorded.Value.Id)).Resolution.Should().Be("Remboursé.");
    }

    [Fact]
    public async Task A_WITHDRAWN_complaint_is_closed_and_not_resolved()
    {
        // A report counting a withdrawal as treated would overstate how many complaints the
        // institution handled, so WasUpheld stays null.
        var recorded = await RecordAsync();

        await AdvanceAsync(
            recorded.Value!.Id, ComplaintAction.Close,
            text: "Le client a retiré sa réclamation après explication au guichet.");

        var complaint = await ComplaintAsync(recorded.Value.Id);
        complaint.Status.Should().Be(ComplaintStatus.Closed);
        complaint.WasUpheld.Should().BeNull();
        complaint.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_complaint_is_NOT_FOUND()
    {
        var result = await AdvanceAsync(Guid.NewGuid(), ComplaintAction.Acknowledge);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ComplaintErrors.NotFound);
    }

    // ── the deadlines ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_stored_deadline_does_not_move_when_the_setting_changes()
    {
        // The one figure the mechanism is judged on. Recomputing from today's setting would move
        // the deadline under a complaint already late.
        var recorded = await RecordAsync(acknowledgementDays: 5, resolutionDays: 30);
        var due = recorded.Value!.ResolutionDueAt;

        // A second complaint recorded under a different configuration leaves the first alone.
        await RecordAsync(acknowledgementDays: 1, resolutionDays: 2);

        (await ComplaintAsync(recorded.Value.Id)).ResolutionDueAt.Should().Be(due);
    }

    [Fact]
    public async Task A_breach_counts_whether_the_step_was_late_OR_still_undone()
    {
        // Otherwise a mechanism would improve its figures by never acknowledging at all.
        var complained = Now.AddDays(-40);

        // Never acknowledged, long past both dates.
        await RecordAsync(receivedAt: complained);

        // Acknowledged, but late.
        var late = await RecordAsync(receivedAt: complained);
        await AdvanceAsync(late.Value!.Id, ComplaintAction.Acknowledge);

        await using var db = _factory.CreateContext();

        var report = await new ComplaintReportHandler(db, Clock(Now)).Handle(
            new ComplaintReportQuery(
                DateOnly.FromDateTime(complained.UtcDateTime.AddDays(-1)),
                DateOnly.FromDateTime(Now.UtcDateTime)),
            CancellationToken.None);

        report.Value!.ReceivedCount.Should().Be(2);
        report.Value.AcknowledgementBreaches.Should().Be(2);
        report.Value.ResolutionBreaches.Should().Be(2);
        report.Value.OpenCount.Should().Be(2);
    }

    [Fact]
    public async Task The_report_counts_by_channel_and_by_category_and_medians_the_duration()
    {
        var complained = Now.AddDays(-20);

        var first = await RecordAsync(
            category: ComplaintCategory.MisSelling, channel: ComplaintChannel.Telephone,
            receivedAt: complained);

        await AdvanceAsync(
            first.Value!.Id, ComplaintAction.Resolve, text: "Garantie reprécisée.", wasUpheld: true);

        await RecordAsync(
            category: ComplaintCategory.Premium, channel: ComplaintChannel.Branch,
            receivedAt: complained);

        await using var db = _factory.CreateContext();

        var report = await new ComplaintReportHandler(db, Clock(Now)).Handle(
            new ComplaintReportQuery(
                DateOnly.FromDateTime(complained.UtcDateTime.AddDays(-1)),
                DateOnly.FromDateTime(Now.UtcDateTime)),
            CancellationToken.None);

        var value = report.Value!;
        value.ResolvedCount.Should().Be(1);
        value.UpheldCount.Should().Be(1);
        value.ByChannel.Should().ContainKey(nameof(ComplaintChannel.Telephone));
        value.ByCategory.Should().ContainKey(nameof(ComplaintCategory.MisSelling));

        // Twenty days from the complaint to the answer.
        value.MedianDaysToResolve.Should().BeApproximately(20, 0.1);
    }

    [Fact]
    public async Task An_inverted_report_window_is_refused()
    {
        await using var db = _factory.CreateContext();

        var report = await new ComplaintReportHandler(db, Clock(Now)).Handle(
            new ComplaintReportQuery(new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 1)),
            CancellationToken.None);

        report.IsFailure.Should().BeTrue();
    }

    // ── the chasing job ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_job_warns_BEFORE_a_deadline_rather_than_only_after()
    {
        // A notification sent the day a deadline is breached is a record of failure; one sent
        // while there is still time is the mechanism working.
        var complained = Now.AddDays(-28);
        await RecordAsync(receivedAt: complained, resolutionDays: 30);

        var notified = await RunDeadlineJobAsync();

        notified.Should().ContainSingle();
        notified[0].TemplateKey.Should().Be(ComplaintDeadlineJob.DeadlineTemplate);
        notified[0].UserId.Should().Be(Actor, "the agent who took the complaint down");

        // The reference, the subject and the date — and never the body.
        notified[0].TemplateData!.Should().ContainKey("complaint_reference");
        notified[0].TemplateData.Should().NotContainKey("body");
    }

    [Fact]
    public async Task A_deadline_comfortably_ahead_is_left_alone()
    {
        await RecordAsync(receivedAt: Now, resolutionDays: 30);

        (await RunDeadlineJobAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_ANSWERED_complaint_is_never_chased()
    {
        var recorded = await RecordAsync(receivedAt: Now.AddDays(-40));

        await AdvanceAsync(recorded.Value!.Id, ComplaintAction.Resolve, text: "Traité.", wasUpheld: false);

        (await RunDeadlineJobAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_stage_is_not_chased_twice()
    {
        // Keyed on the complaint AND which deadline, so a daily sweep re-sends nothing.
        await RecordAsync(receivedAt: Now.AddDays(-28), resolutionDays: 30);

        var first = await RunDeadlineJobAsync();
        var second = await RunDeadlineJobAsync();

        first.Should().ContainSingle();
        second.Should().ContainSingle();
        second[0].IdempotencyKey.Should().Be(first[0].IdempotencyKey);
    }

    private async Task<List<NotifyUserRequest>> RunDeadlineJobAsync()
    {
        var notified = new List<NotifyUserRequest>();
        var administration = Substitute.For<IAdministrationModule>();

        administration
            .NotifyUserAsync(Arg.Do<NotifyUserRequest>(notified.Add), Arg.Any<CancellationToken>())
            .Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(administration);
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        await new ComplaintDeadlineJob(
                new ComplaintScopeFactory(provider), NullLogger<ComplaintDeadlineJob>.Instance)
            .RunAsync(Tenant, CancellationToken.None);

        return notified;
    }

    private sealed class ComplaintScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
