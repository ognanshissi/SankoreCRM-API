namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations;
using Sankore.Modules.Kyc.Features.RemoteInvitations.Consumers;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// KRM-B-04: a link is withdrawn when the email it was posted to, or the date of birth it is
/// checked against, changes.
///
/// <para>
/// The negative cases carry as much of the rule as the positive one. Revoking on every sensitive
/// change would make a corrected surname cost the customer a new email and another trip through
/// the flow, for a link that is still perfectly valid.
/// </para>
/// </summary>
public sealed class ClientContactChangedInvitationConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;

    public ClientContactChangedInvitationConsumerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private ClientContactChangedInvitationConsumer Consumer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(new KycRemoteInvitationRevoker(
            _db, TimeProvider.System, NullLogger<KycRemoteInvitationRevoker>.Instance));
        services.AddLogging();

        return new ClientContactChangedInvitationConsumer(
            new FixedScopeFactory(services.BuildServiceProvider()),
            NullLogger<ClientContactChangedInvitationConsumer>.Instance);
    }

    private async Task<KycRemoteInvitation> SeedLinkAsync()
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            productLineId: Guid.NewGuid(), responsibleAgentId: Guid.NewGuid());

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, 72, Guid.Empty, TimeProvider.System);

        _db.KycFiles.Add(file);
        _db.KycRemoteInvitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);

        return invitation;
    }

    private ClientSensitiveDataChangedEvent Changed(params string[] fields)
        => new(_tenantId, _customerId, fields, "Correction guichet", _actorId);

    private async Task<KycRemoteInvitation> ReloadAsync(Guid id)
        => await _db.KycRemoteInvitations.AsNoTracking()
            .FirstAsync(i => i.Id == id, CancellationToken.None);

    [Fact]
    public async Task A_changed_email_withdraws_the_link()
    {
        // The credential is sitting in a mailbox the customer no longer reads — possibly somebody
        // else's.
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(Changed("Email")));

        var reloaded = await ReloadAsync(invitation.Id);
        reloaded.Status.Should().Be(KycRemoteInvitationStatus.Revoked);
        reloaded.RevokeReason.Should().Be(KycRemoteInvitationRevoker.EmailChangedReason);
        reloaded.RevokedBy.Should().Be(_actorId,
            "the revocation is a consequence of an agent's edit and the trail should say whose");
    }

    [Fact]
    public async Task A_changed_date_of_birth_withdraws_the_link()
    {
        // The identification question no longer has the answer the customer was given: they would
        // try their real date, fail five times and lock themselves out of their own enrolment.
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(Changed("DateOfBirth")));

        var reloaded = await ReloadAsync(invitation.Id);
        reloaded.Status.Should().Be(KycRemoteInvitationStatus.Revoked);
        reloaded.RevokeReason.Should().Be(KycRemoteInvitationRevoker.BirthDateChangedReason);
    }

    [Fact]
    public async Task When_both_changed_the_email_reason_is_the_one_recorded()
    {
        // It is the one that says the credential left the building, which is what an agent needs
        // to read first.
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(Changed("DateOfBirth", "Email")));

        (await ReloadAsync(invitation.Id)).RevokeReason
            .Should().Be(KycRemoteInvitationRevoker.EmailChangedReason);
    }

    [Theory]
    [InlineData("LastName")]
    [InlineData("IdentityDocumentNumber")]
    [InlineData("PostalAddress")]
    [InlineData("Phone")]
    public async Task Any_other_sensitive_field_leaves_the_link_alone(string field)
    {
        // These leave the link perfectly valid. Revoking on them would make a routine correction
        // cost the customer a new email.
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(Changed(field)));

        (await ReloadAsync(invitation.Id)).Status
            .Should().Be(KycRemoteInvitationStatus.Scheduled);
    }

    [Fact]
    public async Task The_field_name_is_matched_case_insensitively()
    {
        // The name crosses a module boundary as a string. A casing difference must not silently
        // turn the rule off.
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(Changed("email")));

        (await ReloadAsync(invitation.Id)).Status
            .Should().Be(KycRemoteInvitationStatus.Revoked);
    }

    [Fact]
    public async Task A_customer_with_no_file_is_a_no_op()
    {
        // The common case for this event: most clients have no remote link at all.
        await Consumer().Consume(Delivered.Of(Changed("Email")));

        (await _db.KycRemoteInvitations.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task A_redelivered_event_does_not_overwrite_the_first_motive()
    {
        // Idempotence is business-level here — this module has no inbox table. The aggregate's
        // revocation is a no-op on an already inactive link.
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(Changed("Email")));
        await Consumer().Consume(Delivered.Of(Changed("DateOfBirth")));

        (await ReloadAsync(invitation.Id)).RevokeReason
            .Should().Be(KycRemoteInvitationRevoker.EmailChangedReason);
    }

    [Fact]
    public async Task A_spent_link_is_not_reopened_as_revoked()
    {
        var invitation = await SeedLinkAsync();

        var tracked = await _db.KycRemoteInvitations
            .AsTracking()
            .FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);
        tracked.Expire(new FrozenClock(DateTimeOffset.UtcNow.AddYears(1)));
        await _db.SaveChangesAsync(CancellationToken.None);

        await Consumer().Consume(Delivered.Of(Changed("Email")));

        (await ReloadAsync(invitation.Id)).Status
            .Should().Be(KycRemoteInvitationStatus.Expired);
    }

    [Fact]
    public async Task The_system_placeholder_tenant_is_a_no_op()
    {
        var invitation = await SeedLinkAsync();

        await Consumer().Consume(Delivered.Of(
            new ClientSensitiveDataChangedEvent(
                Guid.Empty, _customerId, ["Email"], "Correction", _actorId)));

        (await ReloadAsync(invitation.Id)).Status
            .Should().Be(KycRemoteInvitationStatus.Scheduled);
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
