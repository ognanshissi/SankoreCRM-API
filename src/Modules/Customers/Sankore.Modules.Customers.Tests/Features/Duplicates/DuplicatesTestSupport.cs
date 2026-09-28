namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Collaborators specific to the deduplication zone, on top of the module-wide doubles in
/// <c>TestSupport/</c>: an event publisher that records instead of sending, a workflow module that
/// can be made to succeed, fail or throw, and a phonetic-key helper so fixtures are keyed exactly
/// the way the backfill job would have keyed them.
/// </summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<IIntegrationEvent> _published = [];

    public IReadOnlyList<IIntegrationEvent> Published => _published;

    public IEnumerable<T> OfType<T>() => _published.OfType<T>();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent
    {
        _published.Add(@event);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hand-written stand-in for the lead-conversion seam. <c>ILeadConversionService</c> is internal, so
/// NSubstitute cannot proxy it (Castle needs <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c> on
/// the module assembly, which is not this zone's to add) — and the merge-chain test never converts a
/// lead anyway: the facade is only used there to resolve a merge chain.
/// </summary>
internal sealed class UnusedLeadConversionService : ILeadConversionService
{
    public Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(
        CreateFromLeadRequest request, CancellationToken ct) =>
        throw new NotSupportedException("Lead conversion is out of scope for the deduplication tests.");
}

internal static class DuplicatesTestDoubles
{
    /// <summary>The production phonetic key calculator — fixtures must be keyed like real rows.</summary>
    internal static readonly IPhoneticKeyCalculator PhoneticKeys = new WestAfricanPhoneticKeyCalculator();

    internal static NullLogger<T> Logger<T>() => NullLogger<T>.Instance;

    /// <summary>Workflow module that starts an instance and returns its id.</summary>
    internal static IWorkflowModule WorkflowStarting(Guid instanceId)
    {
        var workflow = Substitute.For<IWorkflowModule>();
        workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(instanceId));
        return workflow;
    }

    /// <summary>Workflow module with no template for ClientMerge: a failure result, not an exception.</summary>
    internal static IWorkflowModule WorkflowWithoutTemplate()
    {
        var workflow = Substitute.For<IWorkflowModule>();
        workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<Guid>("WORKFLOW_TEMPLATE_NOT_FOUND"));
        return workflow;
    }

    /// <summary>Workflow module that is simply down.</summary>
    internal static IWorkflowModule WorkflowThrowing()
    {
        var workflow = Substitute.For<IWorkflowModule>();
        workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<Guid>>>(_ => throw new InvalidOperationException("workflow module unavailable"));
        return workflow;
    }

    /// <summary>
    /// Seeds an individual whose phonetic keys and date-of-birth blind index are set, i.e. a client
    /// the detection job can actually block on. Same shape the create handler produces.
    /// </summary>
    internal static async Task<Client> SeedDetectableAsync(
        CustomersDbContext db,
        Guid tenantId,
        Guid agencyId,
        string first,
        string last,
        string clientNumber,
        string? dateOfBirthBlindIndex = null,
        string? identityDocumentNumber = null,
        string? fatherName = null,
        string? motherName = null,
        CancellationToken ct = default)
    {
        var client = TestClientFactory.Individual(
            tenantId, agencyId,
            first: first,
            last: last,
            clientNumber: clientNumber,
            identityDocumentNumber: identityDocumentNumber);

        client.SetPhoneticKeys(PhoneticKeys.Compute(last), PhoneticKeys.Compute(first));

        // The date-of-birth blind index is the second blocking key; overriding it lets a test decide
        // whether two clients share a birth date without knowing the date at all.
        if (dateOfBirthBlindIndex is not null)
            OverrideDateOfBirthBlindIndex(db, client, dateOfBirthBlindIndex);

        if (fatherName is not null || motherName is not null)
            OverrideParents(db, client, fatherName, motherName);

        return await TestClientFactory.SeedAsync(db, client, ct);
    }

    /// <summary>
    /// Writes a blind index straight onto the tracked entity. The aggregate exposes no setter for it
    /// (it is assigned once at creation), and the point of the fixture is to control the BLIND value,
    /// never a clear date.
    /// </summary>
    internal static void OverrideDateOfBirthBlindIndex(CustomersDbContext db, Client client, string? blindIndex)
    {
        db.Clients.Attach(client);
        db.Entry(client).Property(c => c.DateOfBirthBlindIndex).CurrentValue = blindIndex;
    }

    internal static void OverrideParents(CustomersDbContext db, Client client, string? fatherName, string? motherName)
    {
        db.Clients.Attach(client);
        if (fatherName is not null)
            db.Entry(client).Property(c => c.FatherName).CurrentValue = fatherName;
        if (motherName is not null)
            db.Entry(client).Property(c => c.MotherName).CurrentValue = motherName;
    }

    /// <summary>Canonical pair ordering, the same rule the candidate table stores.</summary>
    internal static (Guid A, Guid B) Canonical(Guid x, Guid y) =>
        x.CompareTo(y) <= 0 ? (x, y) : (y, x);
}
