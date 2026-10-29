namespace Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Ingestion.Webhook;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateLeadSourceHandler(LeadsDbContext db, ISecretsModule secrets)
    : IRequestHandler<ActivateLeadSourceCommand, Result>
{
    public async Task<Result> Handle(ActivateLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        // Read only for a server webhook: it is the one mode whose activation depends on it.
        var secretStored = source.Mode != IntegrationMode.ServerWebhook
                           || await WebhookSigning.HasSecretAsync(secrets, source.TenantId, source.Id, ct);

        var missing = source.Activate(secretStored);
        if (missing.Count > 0)
            return Result.Fail($"ACTIVATION_PREREQUISITES_NOT_MET:{string.Join(",", missing)}");

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
