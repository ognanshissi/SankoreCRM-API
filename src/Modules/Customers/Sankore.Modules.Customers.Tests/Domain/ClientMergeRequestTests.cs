namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Xunit;

public class ClientMergeRequestTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid RequesterId = Guid.NewGuid();
    private static readonly Guid ApproverId = Guid.NewGuid();

    [Fact]
    public void Open_should_start_pending_approval()
    {
        var request = NewRequest();

        request.Status.Should().Be(MergeRequestStatus.PendingApproval);
        request.RequestedBy.Should().Be(RequesterId);
        request.DecidedBy.Should().BeNull();
    }

    [Fact]
    public void Open_should_refuse_merging_a_client_into_itself()
    {
        var clientId = Guid.NewGuid();
        var act = () => ClientMergeRequest.Open(TenantId, clientId, clientId, "{}", RequesterId);

        act.Should().Throw<Sankore.Shared.Kernel.DomainException>()
            .Which.MessageKey.Should().Be("ClientMergeRequest.Self.Forbidden");
    }

    [Fact]
    public void Approve_by_the_requester_should_be_forbidden()
    {
        var request = NewRequest();

        var result = request.Approve(RequesterId, "OK pour moi", DateTimeOffset.UtcNow);

        result.Error.Should().Be(CustomerErrors.SelfApprovalForbidden);
        request.Status.Should().Be(MergeRequestStatus.PendingApproval);
    }

    [Fact]
    public void Approve_by_somebody_else_should_approve_the_request()
    {
        var request = NewRequest();
        var at = DateTimeOffset.UtcNow;

        var result = request.Approve(ApproverId, " Doublon confirmé ", at);

        result.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(MergeRequestStatus.Approved);
        request.DecidedBy.Should().Be(ApproverId);
        request.DecidedAt.Should().Be(at);
        request.DecisionComment.Should().Be("Doublon confirmé");
    }

    [Fact]
    public void A_second_decision_should_be_rejected()
    {
        var request = NewRequest();
        request.Approve(ApproverId, null, DateTimeOffset.UtcNow);

        request.Approve(Guid.NewGuid(), null, DateTimeOffset.UtcNow)
            .Error.Should().Be(CustomerErrors.MergeAlreadyDecided);
        request.Reject(Guid.NewGuid(), null, DateTimeOffset.UtcNow)
            .Error.Should().Be(CustomerErrors.MergeAlreadyDecided);
    }

    [Fact]
    public void Reject_should_close_the_request()
    {
        var request = NewRequest();

        var result = request.Reject(ApproverId, "Deux personnes distinctes", DateTimeOffset.UtcNow);

        result.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(MergeRequestStatus.Rejected);
    }

    [Fact]
    public void MarkExecuted_should_only_follow_an_approval()
    {
        var request = NewRequest();
        request.MarkExecuted(DateTimeOffset.UtcNow).Error.Should().Be(CustomerErrors.InvalidStatusTransition);

        request.Approve(ApproverId, null, DateTimeOffset.UtcNow);
        var at = DateTimeOffset.UtcNow;

        request.MarkExecuted(at).IsSuccess.Should().BeTrue();
        request.Status.Should().Be(MergeRequestStatus.Executed);
        request.ExecutedAt.Should().Be(at);
    }

    [Fact]
    public void LinkWorkflow_should_store_the_instance_id()
    {
        var request = NewRequest();
        var instanceId = Guid.NewGuid();

        request.LinkWorkflow(instanceId);

        request.WorkflowInstanceId.Should().Be(instanceId);
    }

    private static ClientMergeRequest NewRequest() =>
        ClientMergeRequest.Open(TenantId, Guid.NewGuid(), Guid.NewGuid(), """{"phone":"survivor"}""", RequesterId);
}
