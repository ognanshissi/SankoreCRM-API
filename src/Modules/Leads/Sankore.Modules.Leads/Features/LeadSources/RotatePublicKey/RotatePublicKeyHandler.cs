namespace Sankore.Modules.Leads.Features.LeadSources.RotatePublicKey;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class RotatePublicKeyHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<RotatePublicKeyCommand, Result<string>>
{
    public async Task<Result<string>> Handle(RotatePublicKeyCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail<string>("LEAD_SOURCE_NOT_FOUND");

        var oldHint = source.PublicKey is not null ? Mask(source.PublicKey) : null;
        var newKey = LeadSourceConfig.GeneratePublicKey();

        source.RotatePublicKey(newKey);

        await publisher.PublishAsync(new LeadSourcePublicKeyRotatedEvent(
            SourceId:   source.Id,
            TenantId:   source.TenantId,
            OldKeyHint: oldHint,
            NewKeyHint: Mask(newKey)), ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(newKey);
    }

    private static string Mask(string value)
    {
        if (value.Length <= 8) return "****";
        return value[..4] + "****" + value[^4..];
    }
}
