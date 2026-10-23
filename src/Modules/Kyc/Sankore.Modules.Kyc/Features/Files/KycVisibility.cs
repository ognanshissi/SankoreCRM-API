namespace Sankore.Modules.Kyc.Features.Files;

using System.Linq.Expressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// A read of ONE KYC file, by id. <see cref="KycVisibilityBehavior{TRequest,TResponse}"/> answers
/// <c>KYC_FILE_NOT_FOUND</c> before the handler runs when the caller may not see that file. The
/// agency perimeter stays the handler's (or <see cref="KycFileAccess"/>'s) own check.
/// </summary>
internal interface IKycFileReadRequest
{
    Guid KycFileId { get; }
}

/// <summary>
/// Which KYC files a caller sees inside their agency perimeter. Without <c>kyc:read:all</c>
/// (Agent, CommercialAgent): the files they are responsible for, and while a file has no
/// responsible agent, the one they created or submitted. With it: the whole perimeter, as before.
///
/// <para>
/// A consequence the product owner chose (2026-10-23): an agent no longer sees a colleague's file
/// waiting on the agent rung of the approval ladder, so that rung is signed by the branch manager —
/// <c>KycApproverLevels</c> gives them the rank for the « awaits me » badge.
/// </para>
/// </summary>
public sealed class KycVisibility(ICurrentUser currentUser)
{
    public bool SeesAll => RecordVisibility.SeesAll(currentUser, Permissions.CanReadAllKycFiles);

    /// <summary>The caller's files only, as a SQL predicate — a list filters before it counts.</summary>
    public IQueryable<KycFile> Apply(IQueryable<KycFile> files)
        => SeesAll ? files : files.Where(ResponsibleIs(currentUser.Id));

    internal static Expression<Func<KycFile, bool>> ResponsibleIs(Guid me)
        => f => f.ResponsibleAgentId == me
                || (f.ResponsibleAgentId == null && (f.CreatedBy == me || f.LastSubmittedBy == me));
}

/// <summary>
/// Refuses an <see cref="IKycFileReadRequest"/> on a file outside the caller's visibility with
/// <c>KYC_FILE_NOT_FOUND</c> — never a 403, which would confirm the file exists.
/// </summary>
internal sealed class KycVisibilityBehavior<TRequest, TResponse>(IServiceProvider services)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IKycFileReadRequest read)
            return await next();

        // Resolved here and not injected: this behavior sits in front of every request of every
        // module, and only a KYC read needs a KycDbContext.
        var visibility = services.GetRequiredService<KycVisibility>();
        if (visibility.SeesAll)
            return await next();

        var me = services.GetRequiredService<ICurrentUser>().Id;
        var visible = await services.GetRequiredService<KycDbContext>().KycFiles
            .AsNoTracking()
            .Where(f => f.Id == read.KycFileId)
            .AnyAsync(KycVisibility.ResponsibleIs(me), cancellationToken);

        return visible ? await next() : ResultFailures.Of<TResponse>(KycErrors.FileNotFound);
    }
}
