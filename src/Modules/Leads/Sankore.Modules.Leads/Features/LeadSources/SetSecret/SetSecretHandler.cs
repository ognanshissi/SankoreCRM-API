namespace Sankore.Modules.Leads.Features.LeadSources.SetSecret;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SetSecretHandler(
    LeadsDbContext db,
    ISecretsModule secrets,
    ITenantContext tenant)
    : IRequestHandler<SetSecretCommand, Result<SecretHint>>
{
    public async Task<Result<SecretHint>> Handle(SetSecretCommand cmd, CancellationToken ct)
    {
        var exists = await db.LeadSourceConfigs
            .AnyAsync(s => s.Id == cmd.SourceId, ct);

        if (!exists)
            return Result.Fail<SecretHint>("LEAD_SOURCE_NOT_FOUND");

        var key = new SecretKey(tenant.CurrentTenantId, "LeadSource", cmd.SourceId, cmd.Name);
        await secrets.SetAsync(key, cmd.Value, cmd.ExpiresAt, ct);

        var hint = await secrets.GetHintAsync(key, ct);
        return Result.Ok(hint!);
    }
}
