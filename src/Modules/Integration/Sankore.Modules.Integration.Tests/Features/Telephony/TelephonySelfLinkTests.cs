namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.MyStatus;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>« Lier mon compte » : an agent pairs themselves, the e-mail address being the only proof.</summary>
public sealed class TelephonySelfLinkTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly FakeAdapter _adapter = new();

    public TelephonySelfLinkTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    private static UserEmailMatch Match(Guid userId, string email) => new(userId, email, "Agent", AgencyId: null, IsActive: true);

    private async Task<Result<LinkMyTelephonyAccountResponse>> LinkMeAsync(params UserEmailMatch[] matches)
    {
        var administration = Substitute.For<IAdministrationModule>();
        administration.FindUsersByEmailAsync(_tenant, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(matches);

        await using var db = _factory.CreateContext();
        var me = TelephonyTestHarness.User(_tenant, _me);

        // The pairing goes through the administrator's command, for real.
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<LinkTelephonyUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => new LinkTelephonyUserHandler(
                    db, TelephonyTestHarness.Access(db, _adapter), me, TelephonyTestHarness.Clock, NullLogger<LinkTelephonyUserHandler>.Instance)
                .Handle(ci.ArgAt<LinkTelephonyUserCommand>(0), CancellationToken.None));

        return await new LinkMyTelephonyAccountHandler(
                TelephonyTestHarness.Access(db, _adapter), administration, me, sender, NullLogger<LinkMyTelephonyAccountHandler>.Instance)
            .Handle(new LinkMyTelephonyAccountCommand(), CancellationToken.None);
    }

    [Fact]
    public async Task The_account_carrying_my_address_alone_is_linked_to_me()
    {
        var result = await LinkMeAsync(Match(_me, "Agent@Example.Test"));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.DisplayName.Should().Be("Agent de test");

        await using var db = _factory.CreateContext();
        var link = await db.TelUserLinks.SingleAsync();
        link.CrmUserId.Should().Be(_me);
        link.ExternalUserId.Should().Be("fake-user-1");
    }

    [Fact]
    public async Task An_address_that_is_someone_elses_links_nothing()
    {
        (await LinkMeAsync(Match(Guid.NewGuid(), FakeAdapter.SeededUser.Email!))).Error.Should().Be(TelephonyErrors.SelfLinkNoMatch);
        (await LinkMeAsync()).Error.Should().Be(TelephonyErrors.SelfLinkNoMatch);

        await using var db = _factory.CreateContext();
        (await db.TelUserLinks.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_address_two_crm_users_share_is_left_to_an_administrator()
    {
        var result = await LinkMeAsync(Match(_me, FakeAdapter.SeededUser.Email!), Match(Guid.NewGuid(), FakeAdapter.SeededUser.Email!));

        result.Error.Should().Be(TelephonyErrors.SelfLinkAmbiguous);
    }

    [Fact]
    public async Task Two_provider_accounts_with_my_address_are_left_to_an_administrator()
    {
        _adapter.TelephonyUsers.Add(FakeAdapter.SeededUser with { Id = new ExternalId("fake-user-2") });

        (await LinkMeAsync(Match(_me, FakeAdapter.SeededUser.Email!))).Error.Should().Be(TelephonyErrors.SelfLinkAmbiguous);
    }

    [Fact]
    public async Task An_account_already_linked_to_a_colleague_is_not_taken_over()
    {
        await using (var db = _factory.CreateContext())
        {
            var linked = await new LinkTelephonyUserHandler(
                    db, TelephonyTestHarness.Access(db, _adapter), TelephonyTestHarness.User(_tenant), TelephonyTestHarness.Clock,
                    NullLogger<LinkTelephonyUserHandler>.Instance)
                .Handle(new LinkTelephonyUserCommand(Guid.NewGuid(), "fake-user-1"), CancellationToken.None);
            linked.IsSuccess.Should().BeTrue();
        }

        (await LinkMeAsync(Match(_me, FakeAdapter.SeededUser.Email!))).Error.Should().Be(TelephonyErrors.UserLinkAlreadyTaken);
    }
}
