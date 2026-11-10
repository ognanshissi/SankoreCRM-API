namespace Sankore.Modules.Leads.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>KYC switched off for the institution (docs/kyc-par-institution.md): its open KYC tasks go.</summary>
public sealed class KycDisabledTasksTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public KycDisabledTasksTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Open_KYC_tasks_are_cancelled_the_others_stay_and_a_workflow_step_is_told()
    {
        var stepId = Guid.NewGuid();
        var kycTask = CrmTask.ForWorkflowStep(_tenant, "Compléter le dossier KYC", "", TaskEntityType.KycFile,
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), stepId, WorkflowTaskRequested.TriggerType);
        var clientTask = CrmTask.ForWorkflowStep(_tenant, "Appeler le client", "", TaskEntityType.Client,
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid(), WorkflowTaskRequested.TriggerType);
        var doneKyc = CrmTask.ForWorkflowStep(_tenant, "Déjà revu", "", TaskEntityType.KycFile,
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), Guid.NewGuid(), WorkflowTaskRequested.TriggerType);
        doneKyc.Complete();
        await using (var db = _factory.CreateContext())
        {
            db.CrmTasks.AddRange(kycTask, clientTask, doneKyc);
            await db.SaveChangesAsync();
        }

        var publisher = Substitute.For<IEventPublisher>();
        await using (var db = _factory.CreateContext())
            (await TenantKycDisabledTasksConsumer.CancelOpenAsync(db, publisher, _tenant, CancellationToken.None)).Should().Be(1);

        await using var read = _factory.CreateContext();
        (await read.CrmTasks.SingleAsync(t => t.Id == kycTask.Id)).Status.Should().Be(CrmTaskStatus.Cancelled);
        (await read.CrmTasks.SingleAsync(t => t.Id == clientTask.Id)).Status.Should().Be(CrmTaskStatus.Pending);
        (await read.CrmTasks.SingleAsync(t => t.Id == doneKyc.Id)).Status.Should().Be(CrmTaskStatus.Completed);
        await publisher.Received(1).PublishAsync(
            Arg.Is<WorkflowStepTaskClosed>(e => e.InstanceStepId == stepId), Arg.Any<CancellationToken>());
    }
}
