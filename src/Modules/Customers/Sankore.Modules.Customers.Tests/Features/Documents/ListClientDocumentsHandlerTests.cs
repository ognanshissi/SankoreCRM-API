namespace Sankore.Modules.Customers.Tests.Features.Documents;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Documents.ListDocuments;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The client's documents as one list (product owner, 2026-10-26): every source listed, a line the
/// caller may not open kept but stripped of its file name and path.
/// </summary>
public sealed class ListClientDocumentsHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IInsuranceGateway _insurance = Substitute.For<IInsuranceGateway>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly Guid _clientId;

    public ListClientDocumentsHandlerTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;

        _integration.Insurance.Returns(_insurance);
        _kyc.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>()).Returns(Array.Empty<CustomerDocumentEntry>());
        _insurance.ListCustomerDocumentsAsync(_clientId, Arg.Any<CancellationToken>()).Returns(Array.Empty<CustomerDocumentEntry>());
        _leads.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>()).Returns(Array.Empty<CustomerDocumentEntry>());
        _notifications.ListClientMessagesAsync(_tenant, _clientId, Arg.Any<CancellationToken>()).Returns(Array.Empty<ClientMessageSummary>());
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Result<ClientDocumentsDto>> ListAsync(
        string[]? permissions = null, ITenantModules? modules = null, Guid[]? perimeter = null)
    {
        await using var db = _factory.CreateContext();
        var user = TestDoubles.CurrentUser(_tenant, _actor);
        user.Permissions.Returns(permissions ?? [Permissions.CanRevealKycDocumentNumber.Code, Permissions.CanViewInsurancePolicy.Code]);

        var handler = new ListClientDocumentsHandler(
            db, user, TestDoubles.AgencyScope(perimeter ?? []), modules ?? FixedTenantModules.All,
            NullLogger<ListClientDocumentsHandler>.Instance, _kyc, _integration, _leads, _notifications);

        return await handler.Handle(new ListClientDocumentsQuery(_clientId), CancellationToken.None);
    }

    private static CustomerDocumentEntry Entry(
        string key, string source, DateTimeOffset at, string? permission, string? path = "some/route",
        CustomerDocumentDirection direction = CustomerDocumentDirection.Collected)
        => new(key, source, direction, "Kind", "CNI_Awa_Kone.jpg", "image/jpeg", 10, at,
               null, null, "KycFile", Guid.NewGuid(), null, path, permission is null ? [] : [permission]);

    [Fact]
    public async Task Every_source_is_folded_into_one_list_newest_first()
    {
        _kyc.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("kyc:1", "Kyc", new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Permissions.CanRevealKycDocumentNumber.Code)]);
        _insurance.ListCustomerDocumentsAsync(_clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("information-sheet:1", "Insurance", new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                Permissions.CanViewInsurancePolicy.Code, direction: CustomerDocumentDirection.Delivered)]);
        _leads.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("project-attachment:1", "Project", new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), null)]);

        var items = (await ListAsync()).Value.Items;

        items.Select(i => i.Key).Should().Equal("information-sheet:1", "project-attachment:1", "kyc:1");
        items[0].Direction.Should().Be("Delivered");
    }

    [Fact]
    public async Task A_line_the_caller_may_not_open_stays_listed_without_its_name_or_path()
    {
        _kyc.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("kyc:1", "Kyc", DateTimeOffset.UtcNow, Permissions.CanRevealKycDocumentNumber.Code)]);

        var line = (await ListAsync(permissions: [])).Value.Items.Should().ContainSingle().Subject;

        line.CanOpen.Should().BeFalse();
        line.FileName.Should().BeNull("a file name often carries the person's name or an identity number");
        line.ContentPath.Should().BeNull();
        line.Kind.Should().Be("Kind", "what the document is stays readable");
    }

    [Fact]
    public async Task A_line_with_its_permission_opens_through_the_source_route()
    {
        _kyc.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("kyc:1", "Kyc", DateTimeOffset.UtcNow, Permissions.CanRevealKycDocumentNumber.Code, "kyc-files/x/documents/y")]);

        var line = (await ListAsync()).Value.Items.Single();

        line.CanOpen.Should().BeTrue();
        line.ContentPath.Should().Be("kyc-files/x/documents/y");
        line.FileName.Should().Be("CNI_Awa_Kone.jpg");
    }

    [Fact]
    public async Task A_record_with_no_file_never_opens()
    {
        _insurance.ListCustomerDocumentsAsync(_clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("consent:1", "Insurance", DateTimeOffset.UtcNow, Permissions.CanViewInsurancePolicy.Code, path: null)]);

        (await ListAsync()).Value.Items.Single().CanOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_source_leaves_the_others_and_says_the_list_is_partial()
    {
        _leads.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CustomerDocumentEntry>>>(_ => throw new InvalidOperationException("down"));
        _kyc.ListCustomerDocumentsAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns([Entry("kyc:1", "Kyc", DateTimeOffset.UtcNow, null)]);

        var result = (await ListAsync()).Value;

        result.Items.Should().ContainSingle();
        result.UnavailableSources.Should().Equal("Leads");
    }

    [Fact]
    public async Task Insurance_is_not_read_for_a_tenant_that_never_had_the_module()
    {
        await ListAsync(modules: FixedTenantModules.None);

        await _insurance.DidNotReceiveWithAnyArgs().ListCustomerDocumentsAsync(default, default);
        await _kyc.ReceivedWithAnyArgs().ListCustomerDocumentsAsync(default, default, default);
    }

    [Fact]
    public async Task A_message_the_client_was_sent_is_a_delivered_line_with_nothing_to_open()
    {
        var sentAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        _notifications.ListClientMessagesAsync(_tenant, _clientId, Arg.Any<CancellationToken>())
            .Returns([new ClientMessageSummary(Guid.NewGuid(), "Sms", "kyc.remote-otp", "Sent",
                sentAt.AddMinutes(-1), sentAt, "+225••••01")]);

        var line = (await ListAsync()).Value.Items.Should().ContainSingle().Subject;

        line.Source.Should().Be("Message");
        line.Kind.Should().Be("Message.Sms");
        line.Direction.Should().Be("Delivered");
        line.At.Should().Be(sentAt, "a message is dated by its sending");
        line.Detail.Should().Be("kyc.remote-otp");
        line.OriginLabel.Should().Be("+225••••01");
        line.CanOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_client_outside_the_perimeter_reads_as_not_found()
    {
        (await ListAsync(perimeter: [Guid.NewGuid()])).Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
