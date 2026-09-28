namespace Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;

using Hangfire;
using MediatR;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Enqueues <see cref="BackfillPhoneticKeysJob"/> and returns immediately — the endpoint answers
/// 202 Accepted. The job argument is the opaque tenant id only: no payload, nothing sensitive, so
/// the Hangfire tables never hold personal data.
/// </summary>
internal sealed class BackfillPhoneticKeysHandler(
    IBackgroundJobClient hangfire,
    ICurrentUser currentUser)
    : IRequestHandler<BackfillPhoneticKeysCommand, Result<BackfillPhoneticKeysResult>>
{
    public Task<Result<BackfillPhoneticKeysResult>> Handle(
        BackfillPhoneticKeysCommand command, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        var jobId = hangfire.Enqueue<BackfillPhoneticKeysJob>(job => job.ExecuteAsync(tenantId));

        return Task.FromResult(Result.Ok(new BackfillPhoneticKeysResult(jobId)));
    }
}
