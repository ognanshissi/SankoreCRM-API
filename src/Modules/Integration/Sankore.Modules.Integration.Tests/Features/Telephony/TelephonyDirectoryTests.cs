namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.GetDirectory;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The import of the provider's directory, and the pairing it PROPOSES (TEL-B-05).
///
/// <para>
/// Every case here is about one property: a proposal is a proposal. Nothing in this slice writes
/// a link, because matching on an address is a heuristic and a wrong link attributes one agent's
/// conversations to another.
/// </para>
/// </summary>
public sealed class TelephonyDirectoryTests : IDisposable
{
    private const string SeededEmail = "agent@example.test";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyDirectoryTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_address_one_crm_account_answers_to_is_proposed_and_not_applied()
    {
        var result = await ReadAsync(Match(_agent, SeededEmail));

        var entry = result.Value.Entries.Single();

        entry.ExternalUserId.Should().Be("fake-user-1");
        entry.ProposedCrmUserId.Should().Be(_agent);
        entry.ProposedCrmUserName.Should().Be("Agent de test");
        entry.LinkedCrmUserId.Should().BeNull();

        await using var db = _factory.CreateContext();
        db.TelUserLinks.Should().BeEmpty("a directory read writes nothing");
    }

    [Fact]
    public async Task An_address_several_crm_accounts_answer_to_yields_no_proposal()
    {
        var result = await ReadAsync(
            Match(_agent, SeededEmail), Match(Guid.NewGuid(), SeededEmail));

        // Picking one would be picking whose calls get attributed to whom.
        result.Value.Entries.Single().ProposedCrmUserId.Should().BeNull();
        result.Value.AmbiguousEmails.Should().ContainSingle().Which.Should().Be(SeededEmail);
    }

    [Fact]
    public async Task Casing_does_not_stop_a_pairing_being_proposed()
    {
        // The provider's directory capitalises an address however the person typed it when they
        // signed up, and a miss here reads as a missing CRM account — which sends an
        // administrator to create a second one.
        var result = await ReadAsync(Match(_agent, "Agent@Example.Test"));

        result.Value.Entries.Single().ProposedCrmUserId.Should().Be(_agent);
    }

    [Fact]
    public async Task A_disabled_crm_account_is_proposed_and_flagged_rather_than_hidden()
    {
        var result = await ReadAsync(Match(_agent, SeededEmail, isActive: false));

        var entry = result.Value.Entries.Single();

        entry.ProposedCrmUserId.Should().Be(_agent);
        entry.ProposedCrmUserIsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task An_already_linked_account_says_so_and_is_proposed_to_nobody()
    {
        await LinkAsync(_agent, "fake-user-1");

        var result = await ReadAsync(Match(_agent, SeededEmail));

        var entry = result.Value.Entries.Single();

        entry.LinkedCrmUserId.Should().Be(_agent);

        // One user holds at most one account, so proposing a second pairing would be proposing
        // something the unique index refuses.
        entry.ProposedCrmUserId.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_provider_is_an_outage_and_not_an_empty_directory()
    {
        var adapter = new FakeAdapter
        {
            ForcedOutcome = IntegrationResult.Transient(IntegrationErrors.Timeout),
        };

        await using var db = _factory.CreateContext();

        var result = await new GetTelephonyDirectoryHandler(
                db,
                TelephonyTestHarness.Access(db, adapter),
                Substitute.For<IAdministrationModule>(),
                TelephonyTestHarness.User(_tenant),
                NullLogger<GetTelephonyDirectoryHandler>.Instance)
            .Handle(new GetTelephonyDirectoryQuery(), CancellationToken.None);

        // An empty list would read as "your provider has no users", which is the one conclusion
        // that sends an administrator to delete working links.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.Unavailable);
    }

    [Fact]
    public async Task The_directory_is_refused_when_telephony_is_off()
    {
        await using var db = _factory.CreateContext();

        var result = await new GetTelephonyDirectoryHandler(
                db,
                TelephonyTestHarness.Access(db, adapter: null),
                Substitute.For<IAdministrationModule>(),
                TelephonyTestHarness.User(_tenant),
                NullLogger<GetTelephonyDirectoryHandler>.Instance)
            .Handle(new GetTelephonyDirectoryQuery(), CancellationToken.None);

        result.Error.Should().Be(TelephonyErrors.Disabled);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static UserEmailMatch Match(Guid userId, string email, bool isActive = true)
        => new(userId, email, "Agent de test", AgencyId: null, IsActive: isActive);

    private async Task<Sankore.Shared.Kernel.Result<
        Sankore.Modules.Integration.Features.Telephony.UserLinks.TelephonyDirectoryDto>> ReadAsync(
        params UserEmailMatch[] matches)
    {
        var administration = Substitute.For<IAdministrationModule>();

        administration.FindUsersByEmailAsync(
                _tenant, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(matches);

        await using var db = _factory.CreateContext();

        return await new GetTelephonyDirectoryHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                administration,
                TelephonyTestHarness.User(_tenant),
                NullLogger<GetTelephonyDirectoryHandler>.Instance)
            .Handle(new GetTelephonyDirectoryQuery(), CancellationToken.None);
    }

    private async Task LinkAsync(Guid crmUserId, string externalUserId)
    {
        await using var db = _factory.CreateContext();

        var result = await new LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant),
                TelephonyTestHarness.Clock,
                NullLogger<LinkTelephonyUserHandler>.Instance)
            .Handle(new LinkTelephonyUserCommand(crmUserId, externalUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
