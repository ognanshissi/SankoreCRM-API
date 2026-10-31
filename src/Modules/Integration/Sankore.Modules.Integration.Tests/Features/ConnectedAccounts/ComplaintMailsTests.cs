namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// A complaint's e-mails (2026-10-31): everybody who may read the complaint sees when, which way
/// and from whose mailbox; only the mailbox's owner sees what was written.
/// </summary>
public sealed class ComplaintMailsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _complaint = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly CalendarEventProtector _protector = new(new PrefixEncryptor());

    public ComplaintMailsTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _user.Id.Returns(_me);
        _user.TenantId.Returns(_tenant);
        _customers.CanActorAccessClientAsync(_tenant, _client, _me, Arg.Any<CancellationToken>()).Returns(true);
        _customers.GetComplaintReferenceAsync(_tenant, _client, _complaint, Arg.Any<CancellationToken>())
            .Returns(new ClientComplaintReference(_complaint, "RCL-2026-000042", true, _client));
        _admin.GetUserNamesAsync(_tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_me] = "Awa Conseillère", [_colleague] = "Koffi Collègue" });
    }

    public void Dispose() => _factory.Dispose();

    private sealed class PrefixEncryptor : IFieldEncryptor
    {
        public string? Encrypt(string? plaintext) => plaintext is null ? null : "enc:" + plaintext;
        public string? Decrypt(string? ciphertext) => ciphertext is null ? null : ciphertext["enc:".Length..];
    }

    private async Task SeedAsync(Guid owner, string subject, int minutesAgo)
    {
        await using var db = _factory.CreateContext();
        var at = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero).AddMinutes(-minutesAgo);
        var message = ConnectedMailMessage.Create(_tenant, Guid.NewGuid(), owner, $"x{minutesAgo}", null, "t1",
            MailFolderKind.Sent, MailDirection.Sent, at, true, false, at);
        message.SetContent(null, null, null, _protector.Protect(subject), _protector.Protect("Bonjour"), null, true, false, null);
        db.ConnectedMailMessages.Add(message);
        db.ConnectedMailLinks.Add(ConnectedMailLink.For(message, MailLinkEntity.Complaint, _complaint));
        await db.SaveChangesAsync();
    }

    private async Task<Sankore.Shared.Kernel.Result<IReadOnlyList<ComplaintMailDto>>> ListAsync()
    {
        await using var db = _factory.CreateContext();
        return await new ListComplaintMailsHandler(db, _user, _protector, _customers, _admin)
            .Handle(new ListComplaintMailsQuery(_client, _complaint), default);
    }

    [Fact]
    public async Task The_owner_reads_their_messages_and_a_colleague_only_when_and_which_way()
    {
        await SeedAsync(_me, "Votre réclamation RCL-2026-000042", minutesAgo: 30);
        await SeedAsync(_colleague, "RE: Votre réclamation RCL-2026-000042", minutesAgo: 10);

        var mails = (await ListAsync()).Value!;

        mails.Should().HaveCount(2);
        mails[0].Should().BeEquivalentTo(new { Mine = false, Subject = (string?)null, MessageId = (Guid?)null, UserName = "Koffi Collègue" });
        mails[1].Mine.Should().BeTrue();
        mails[1].Subject.Should().Be("Votre réclamation RCL-2026-000042");
        mails[1].MessageId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_client_outside_the_perimeter_or_another_clients_complaint_is_not_found()
    {
        _customers.CanActorAccessClientAsync(_tenant, _client, _me, Arg.Any<CancellationToken>()).Returns(false);

        (await ListAsync()).Error.Should().Be(ListComplaintMailsHandler.NotFound);
    }
}
