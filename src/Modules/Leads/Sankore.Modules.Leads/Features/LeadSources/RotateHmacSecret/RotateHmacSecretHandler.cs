namespace Sankore.Modules.Leads.Features.LeadSources.RotateHmacSecret;

using System.Security.Cryptography;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RotateHmacSecretHandler(
    LeadsDbContext db,
    ISecretsModule secrets,
    ITenantContext tenant)
    : IRequestHandler<RotateHmacSecretCommand, Result<RotateHmacSecretResult>>
{
    private const string HmacName = "hmac-signing";
    private const string HmacOldName = "hmac-signing-old";

    public async Task<Result<RotateHmacSecretResult>> Handle(
        RotateHmacSecretCommand cmd, CancellationToken ct)
    {
        var exists = await db.LeadSourceConfigs
            .AnyAsync(s => s.Id == cmd.SourceId, ct);

        if (!exists)
            return Result.Fail<RotateHmacSecretResult>("LEAD_SOURCE_NOT_FOUND");

        var tenantId = tenant.CurrentTenantId;

        // Read current secret to archive it
        var currentKey = new SecretKey(tenantId, "LeadSource", cmd.SourceId, HmacName);
        var currentValue = await secrets.GetValueAsync(currentKey, ct);

        // Generate new 32-byte secret
        var newSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Store old secret with 7-day expiry (dual-accept window)
        var oldExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        if (currentValue is not null)
        {
            var oldKey = new SecretKey(tenantId, "LeadSource", cmd.SourceId, HmacOldName);
            await secrets.SetAsync(oldKey, currentValue, oldExpiresAt, ct);
        }

        // Store new secret (no expiry)
        await secrets.SetAsync(currentKey, newSecret, ct: ct);

        return Result.Ok(new RotateHmacSecretResult(newSecret, oldExpiresAt));
    }
}
