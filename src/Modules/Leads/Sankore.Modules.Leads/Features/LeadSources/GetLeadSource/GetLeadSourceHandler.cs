namespace Sankore.Modules.Leads.Features.LeadSources.GetLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadSourceHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<GetLeadSourceQuery, Result<LeadSourceDetailDto>>
{
    public async Task<Result<LeadSourceDetailDto>> Handle(
        GetLeadSourceQuery query, CancellationToken ct)
    {
        // Tenant query filter ensures cross-tenant access returns null → 404
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == query.SourceId, ct);

        if (source is null)
            return Result.Fail<LeadSourceDetailDto>("SOURCE_NOT_FOUND");

        var now = clock.GetUtcNow();
        var sevenDaysAgo = now.AddDays(-7);

        var lastReceived = await db.LeadIngestions
            .Where(i => i.SourceId == source.Id && i.Status == LeadIngestionStatus.Accepted)
            .OrderByDescending(i => i.IngestedAt)
            .Select(i => (DateTimeOffset?)i.IngestedAt)
            .FirstOrDefaultAsync(ct);

        var volume = await db.LeadIngestions
            .CountAsync(i => i.SourceId == source.Id
                          && i.Status == LeadIngestionStatus.Accepted
                          && i.IngestedAt >= sevenDaysAgo, ct);

        var health = ListLeadSourcesHandler.ComputeHealth(source.Status, lastReceived, now);

        // Build secret hints from settings vault references — never expose actual values
        var secrets = BuildSecretHints(source.Settings);

        return Result.Ok(new LeadSourceDetailDto(
            source.Id, source.Code, source.Label, source.Description,
            source.ChannelType, source.Mode, source.Status, health,
            lastReceived, volume,
            source.PublicKey, source.Settings, source.PlatformConnectionId,
            source.DedupWindowDays, source.CostPerLead,
            source.IsSystem, source.DisplayOrder, source.Version, source.CreatedAt,
            secrets));
    }

    private static IReadOnlyList<SecretHintDto> BuildSecretHints(SourceSettings? settings)
    {
        if (settings is null) return [];

        var hints = new List<SecretHintDto>();

        switch (settings)
        {
            case ServerWebhookSettings wh when wh.SignatureCredentialVaultRef is not null:
                hints.Add(new SecretHintDto(
                    "SignatureCredential",
                    MaskVaultRef(wh.SignatureCredentialVaultRef),
                    null, null));
                break;

            case ScheduledPullSettings pull when pull.AuthCredentialVaultRef is not null:
                hints.Add(new SecretHintDto(
                    "AuthCredential",
                    MaskVaultRef(pull.AuthCredentialVaultRef),
                    null, null));
                break;

            case PlatformSettings plat when plat.OAuthCredentialVaultRef is not null:
                hints.Add(new SecretHintDto(
                    "OAuthCredential",
                    MaskVaultRef(plat.OAuthCredentialVaultRef),
                    null, null));
                break;
        }

        return hints;
    }

    private static string MaskVaultRef(string vaultRef)
    {
        if (vaultRef.Length <= 8) return "****";
        return vaultRef[..4] + "****" + vaultRef[^4..];
    }
}
