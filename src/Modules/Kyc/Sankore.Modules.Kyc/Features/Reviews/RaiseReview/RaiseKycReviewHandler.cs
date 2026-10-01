namespace Sankore.Modules.Kyc.Features.Reviews.RaiseReview;

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RaiseKycReviewHandler(
    KycDbContext db,
    KycReviewScheduler scheduler,
    IBackgroundJobClient hangfire,
    TimeProvider clock,
    ILogger<RaiseKycReviewHandler> logger)
    : IRequestHandler<RaiseKycReviewCommand, Result<RaiseKycReviewResult>>
{
    public async Task<Result<RaiseKycReviewResult>> Handle(
        RaiseKycReviewCommand cmd, CancellationToken ct)
    {
        // Re-checked here and not only in the validator: a review with no stated cause is a task
        // the compliance officer cannot act on, and a future caller reaching this handler outside
        // the validation pipeline would create one.
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail<RaiseKycReviewResult>(ReviewErrors.ReasonRequired);

        // The tenant query filter applies: this one comes from an HTTP request, unlike the sweep.
        var file = await db.KycFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId, ct);

        // 404 and never 403 for a file of another tenant — "forbidden" would confirm that a
        // customer exists there.
        if (file is null)
            return Result.Fail<RaiseKycReviewResult>(KycErrors.FileNotFound);

        if (!file.IsOpen)
            return Result.Fail<RaiseKycReviewResult>(ReviewErrors.FileNotOpen);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var review = scheduler.ScheduleEvent(file, today, cmd.Reason.Trim());

        await db.SaveChangesAsync(ct);

        // The reason is deliberately absent from the log line: it is operator-entered free text,
        // and logs travel further than the compliance screen it was written for.
        // Sweep this tenant now instead of waiting for 01:00. An operator raising a review has a
        // reason that will not keep — a transaction alert, a document that just turned out to be
        // false — and leaving the agency unaware for up to 24 hours defeats the point of letting
        // them raise one by hand. The job is the SAME idempotent sweep the orchestrator enqueues,
        // so running it twice today costs one query and publishes nothing a second time.
        hangfire.Enqueue<ProcessTenantKycReviewsJob>(job => job.ExecuteAsync(file.TenantId));

        logger.LogInformation(
            "Event-driven KYC review {ReviewId} raised on file {KycFileId} by {UserId}, due {DueDate}; "
            + "a sweep of tenant {TenantId} was queued so the agency is told today.",
            review.Id, file.Id, cmd.RaisedBy, review.DueDate, file.TenantId);

        return Result.Ok(new RaiseKycReviewResult(review.Id, review.DueDate));
    }
}
