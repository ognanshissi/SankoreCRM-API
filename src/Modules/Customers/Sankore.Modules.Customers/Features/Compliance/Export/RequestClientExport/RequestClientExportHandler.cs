namespace Sankore.Modules.Customers.Features.Compliance.Export.RequestClientExport;

using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class RequestClientExportHandler(
    CustomersDbContext db,
    ICustomerSettings settings,
    ICurrentUser currentUser,
    IBackgroundJobClient backgroundJobs,
    ILogger<RequestClientExportHandler> logger
) : IRequestHandler<RequestClientExportCommand, Result<RequestClientExportResult>>
{
    private static readonly JsonSerializerOptions FilterJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>One day: a download link that outlives a working day is a link that leaks.</summary>
    private const int MaxTtlMinutes = 24 * 60;

    public async Task<Result<RequestClientExportResult>> Handle(
        RequestClientExportCommand command, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        var ttlMinutes = await settings.GetIntAsync(
            tenantId, CustomerSettingKeys.ExportLinkTtlMinutes, ct);

        ttlMinutes = Math.Clamp(ttlMinutes, 1, MaxTtlMinutes);

        // The filters are stored, not re-passed through the Hangfire payload: a job argument is
        // persisted in clear in the job store, and a phone or document filter is personal data.
        // The job receives the export id and reads the filters back from the row.
        var filtersJson = JsonSerializer.Serialize(command.Filters, FilterJsonOptions);

        // Queue() mints the download token and sets ExpiresAt = now + ttl; the token is the only
        // thing protecting the file, so the domain owns its generation.
        var job = ClientExportJob.Queue(tenantId, currentUser.Id, filtersJson, TimeSpan.FromMinutes(ttlMinutes));

        db.ClientExportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        // Enqueued after the row is written. TransactionBehavior still owns the outer
        // transaction, so a rollback would leave an orphan job: GenerateClientExportJob handles
        // that by logging "not found" and returning, rather than retrying forever.
        backgroundJobs.Enqueue<GenerateClientExportJob>(j => j.ExecuteAsync(job.Id, tenantId));

        logger.LogInformation(
            "Client export {ExportId} queued for tenant {TenantId} by {RequestedBy}; link expires at {ExpiresAt:O}.",
            job.Id, tenantId, currentUser.Id, job.ExpiresAt);

        return Result.Ok(new RequestClientExportResult(
            ExportId: job.Id,
            Status: job.Status.ToString(),
            RequestedAt: job.RequestedAt,
            ExpiresAt: job.ExpiresAt));
    }
}
