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

        // Read only when the script carries a captcha: without its secret every lead is refused.
        var captchaStored = source.Settings is not EmbeddedScriptSettings { CaptchaProviderName: not null }
                            || await secrets.GetHintAsync(new SecretKey(source.TenantId, "LeadSource", source.Id,
                                   Features.Ingestion.Web.CaptchaSecrets.Name), ct) is not null;

        var missing = source.Activate(secretStored, captchaStored);
        if (missing.Count > 0)
            return Result.Fail($"ACTIVATION_PREREQUISITES_NOT_MET:{string.Join(",", missing)}");

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
