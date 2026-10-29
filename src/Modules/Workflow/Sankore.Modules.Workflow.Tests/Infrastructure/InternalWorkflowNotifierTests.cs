namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The people WORKING a record are told in their bell: its agent, and the holders of a role in the
/// record's agency (or above it). The person OF the record is the owner module's business.
/// </summary>
public sealed class InternalWorkflowNotifierTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();

    public InternalWorkflowNotifierTests()
    {
        _notifications.NotifyInAppAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
        _administration.GetUserNamesAsync(default, default!, default).ReturnsForAnyArgs(
            ci => ((IReadOnlyCollection<Guid>)ci[1]).ToDictionary(id => id, _ => "Koffi Agent") as IReadOnlyDictionary<Guid, string>);
    }

    [Fact]
    public async Task The_agent_and_the_role_holders_of_the_lead_agency_are_told_once_each()
    {
        var leadId = Guid.NewGuid();
        var agency = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var manager = Guid.NewGuid();
        _leads.GetNotificationFactsAsync(_tenantId, "Lead", leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadNotificationFacts(agency, agent, "Awa", "Ndiaye", "Crédit", $"/leads/{leadId}"));
        _administration.ListActiveUsersInRolesAsync(_tenantId, Arg.Any<IReadOnlyCollection<string>>(), agency, Arg.Any<CancellationToken>())
            .Returns([manager, agent]);

        var request = Request("Lead", leadId,
            [WorkflowNotificationRecipients.AssignedAgent, WorkflowNotificationRecipients.Role],
            subject: "Nouveau dossier {{nom}}", message: "{{prenom}} {{nom}} attend {{agent}} pour {{produit}}.");

        var told = await Notifier().SendAsync(request, default);

        told.Should().Be(2);
        await _notifications.Received(2).NotifyInAppAsync(Arg.Is<InAppNotificationRequest>(r =>
            r.Title == "Nouveau dossier Ndiaye"
            && r.Body == "Awa Ndiaye attend Koffi Agent pour Crédit."
            && r.Link == $"/leads/{leadId}"
            && r.Category == InternalWorkflowNotifier.Category
            && r.IdempotencyKey == $"{request.IdempotencyKey}:user:{r.UserId}"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_the_person_of_the_record_named_tells_nobody_here()
    {
        var told = await Notifier().SendAsync(Request("Lead", Guid.NewGuid(), [WorkflowNotificationRecipients.Subject]), default);

        told.Should().Be(0);
        await _notifications.DidNotReceiveWithAnyArgs().NotifyInAppAsync(default!, default);
    }

    [Fact]
    public async Task A_client_record_reaches_its_advisor_with_the_step_name_as_title()
    {
        var clientId = Guid.NewGuid();
        var advisor = Guid.NewGuid();
        _customers.GetClientSummaryAsync(_tenantId, clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(clientId, "C-1", "Individual", "DIOP Moussa", "Active", Guid.NewGuid(), advisor, "Approved", "Low", null));

        var told = await Notifier().SendAsync(Request("Account", clientId, [WorkflowNotificationRecipients.AssignedAgent]), default);

        told.Should().Be(1);
        await _notifications.Received(1).NotifyInAppAsync(Arg.Is<InAppNotificationRequest>(r =>
            r.UserId == advisor && r.Title == "Prévenir" && r.Link == $"/customers/{clientId}"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_kyc_file_tells_the_role_in_the_file_agency()
    {
        var fileId = Guid.NewGuid();
        var agency = Guid.NewGuid();
        var officer = Guid.NewGuid();
        _kyc.GetKycFileSummaryAsync(_tenantId, fileId, Arg.Any<CancellationToken>())
            .Returns(new KycFileSummary(fileId, Guid.NewGuid(), agency, "Validating"));
        _administration.ListActiveUsersInRolesAsync(_tenantId, Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("ComplianceOfficer")), agency, Arg.Any<CancellationToken>())
            .Returns([officer]);

        var told = await Notifier().SendAsync(Request("KycFile", fileId, [WorkflowNotificationRecipients.Role, WorkflowNotificationRecipients.AssignedAgent]), default);

        told.Should().Be(1);
        await _notifications.Received(1).NotifyInAppAsync(Arg.Is<InAppNotificationRequest>(r =>
            r.UserId == officer && r.Link == $"/kyc/{fileId}"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_record_that_no_longer_exists_tells_nobody()
    {
        var told = await Notifier().SendAsync(Request("Lead", Guid.NewGuid(), [WorkflowNotificationRecipients.Role]), default);

        told.Should().Be(0);
        await _administration.DidNotReceiveWithAnyArgs().ListActiveUsersInRolesAsync(default, default!, default, default);
    }

    [Theory]
    [InlineData("Bonjour {{prenom}} {{ NOM }}", "Bonjour Awa Ndiaye")]
    [InlineData("{{agent}} vous rappelle", "vous rappelle")]
    [InlineData("Code {{inconnu}}", "Code {{inconnu}}")]
    [InlineData(null, "")]
    public void Variables_are_replaced_and_unknown_ones_left_as_typed(string? text, string expected)
        => WorkflowNotificationText.Render(text, new WorkflowNotificationValues("Awa", "Ndiaye"))
            .Should().Be(expected);

    private InternalWorkflowNotifier Notifier()
        => new(_leads, _customers, _kyc, _administration, _notifications, Substitute.For<ITenantStore>());

    private WorkflowNotificationRequested Request(
        string entityType, Guid entityId, IReadOnlyList<string> recipients, string? subject = null, string? message = "Un dossier attend.")
        => new(_tenantId, Guid.NewGuid(), Guid.NewGuid(), "Prévenir", entityType, entityId, "Email",
            recipients, ["ComplianceOfficer"], subject, message);
}
