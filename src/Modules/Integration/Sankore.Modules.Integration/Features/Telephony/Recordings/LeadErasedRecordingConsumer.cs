namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// A lead was erased (a person's request, data-purge lot 2): its calls lose their numbers and note,
/// its recordings their bytes — the client path (<see cref="ClientAnonymizedRecordingConsumer"/>),
/// for a lead.
/// </summary>
public sealed class LeadErasedRecordingConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<LeadErasedRecordingConsumer> logger)
    : IConsumer<LeadErasedEvent>
{
    public async Task Consume(ConsumeContext<LeadErasedEvent> context)
    {
        var evt = context.Message;
        if (evt.TenantId == Guid.Empty || evt.LeadId == Guid.Empty) return;

        // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
        using var bgCtx = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        await ClientAnonymizedRecordingConsumer.ApplyToEntityAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetService<IRecordingStore>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            evt.TenantId,
            TelEntityTypes.Lead,
            evt.LeadId,
            context.CancellationToken);
    }
}
