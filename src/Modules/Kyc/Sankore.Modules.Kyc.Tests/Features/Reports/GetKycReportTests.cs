namespace Sankore.Modules.Kyc.Tests.Features.Reports;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reports;
using Sankore.Modules.Kyc.Tests.Features.Reviews;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The « KYC » tab of the reports (reports lot 5, 2026-11-02): what the period figures count, the
/// complements read off the circuit, and where files wait today.
/// </summary>
public sealed class GetKycReportTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _submitter = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    public GetKycReportTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static FixedClock At(DateTimeOffset at) => new(at);

    /// <summary>A file scored and in validation, opened at <paramref name="openedAt"/>.</summary>
    private KycFile Validating(DateTimeOffset openedAt, Guid? customerId = null)
    {
        var file = KycFile.Open(_tenantId, customerId ?? Guid.NewGuid(), KycChannel.Agency, _submitter, At(openedAt));
        file.SubmitForVerification(_submitter, At(openedAt));
        file.RecordVerification(85, KycConfidenceLevel.High, At(openedAt));
        return file;
    }

    private KycApprovalStep Step(KycFile file, KycApprovalLevel level, DateTimeOffset createdAt,
        KycApprovalDecision? decision = null, DateTimeOffset? decidedAt = null)
    {
        var step = KycApprovalStep.Pending(_tenantId, file.Id, level, At(createdAt));
        if (decision is { } d) step.Decide(d, _approver, At(decidedAt ?? createdAt));
        return step;
    }

    private async Task SaveAsync(params object[] entities)
    {
        await using var db = _factory.CreateContext();
        foreach (var e in entities) db.Add(e);
        await db.SaveChangesAsync();
    }

    private async Task<KycReportDto> RunAsync(DateTimeOffset now)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.CurrentTenantId.Returns(_tenantId);
        var handler = new GetKycReportHandler(
            _factory.CreateContext(), tenant, Substitute.For<IAdministrationModule>(), _customers, At(now));
        var result = await handler.Handle(new GetKycReportQuery(From, To, null), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    [Fact]
    public async Task Validations_refusals_and_complements_are_counted_in_their_period()
    {
        // Sent back once, then validated two days after opening.
        var complemented = Validating(From.AddDays(1));
        complemented.Approve(KycTier.Full, _approver, At(From.AddDays(3))).IsSuccess.Should().BeTrue();
        // Validated at the first pass.
        var firstPass = Validating(From.AddDays(2));
        firstPass.Approve(KycTier.Simplified, _approver, At(From.AddDays(4))).IsSuccess.Should().BeTrue();
        // Refused.
        var refused = Validating(From.AddDays(2));
        refused.Reject(_approver, At(From.AddDays(5))).IsSuccess.Should().BeTrue();
        // Opened and validated in the previous period.
        var before = Validating(From.AddDays(-10));
        before.Approve(KycTier.Full, _approver, At(From.AddDays(-8))).IsSuccess.Should().BeTrue();

        await SaveAsync(complemented, firstPass, refused, before,
            Step(complemented, KycApprovalLevel.Agent, From.AddDays(1), KycApprovalDecision.ComplementRequired, From.AddDays(2)),
            Step(refused, KycApprovalLevel.Agent, From.AddDays(2), KycApprovalDecision.Rejected, From.AddDays(5)));

        var report = await RunAsync(To);

        var k = report.Kpis.Period;
        k.Opened.Should().Be(3);
        k.Validated.Should().Be(2);
        k.Rejected.Should().Be(1);
        k.AvgValidationDays.Should().Be(2);
        k.ComplementRate.Should().Be(33.3, "one of the three files decided was sent back");
        k.FirstPassRate.Should().Be(50);
        report.Previous.Validated.Should().Be(1);
        report.ByTier.Should().BeEquivalentTo([new KycReportCount("Full", 1), new KycReportCount("Simplified", 1)]);
    }

    [Fact]
    public async Task A_file_waits_at_its_next_rung_since_the_previous_signature()
    {
        var customerId = Guid.NewGuid();
        _customers.GetCustomerAsync(_tenantId, customerId, Arg.Any<CancellationToken>())
            .Returns(new CustomerSummary(customerId, "Awa Traoré", null, null, null));
        var file = Validating(From.AddDays(-5), customerId);
        await SaveAsync(file,
            Step(file, KycApprovalLevel.Agent, From.AddDays(-5), KycApprovalDecision.Approved, To.AddDays(-3)),
            Step(file, KycApprovalLevel.BranchManager, From.AddDays(-5)));

        var report = await RunAsync(To);

        report.Kpis.InValidation.Should().Be(1);
        var level = report.Waiting.Single();
        level.Level.Should().Be("BranchManager");
        level.OldestDays.Should().Be(3);
        report.Longest.Single().CustomerName.Should().Be("Awa Traoré");
    }
}
