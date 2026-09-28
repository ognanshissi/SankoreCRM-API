namespace Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Queues the phonetic-key backfill for the caller's tenant (US-M01-BE-23).
/// <para>
/// A command, not a query: it changes data (asynchronously) and must land in the audit trail with
/// the operator who asked for it, hence <see cref="ICommand"/> + <see cref="IResourceCommand"/>.
/// The tenant is never a parameter — it comes from the authenticated caller, so one tenant can
/// never schedule a scan over another's clients.
/// </para>
/// </summary>
public sealed record BackfillPhoneticKeysCommand
    : IRequest<Result<BackfillPhoneticKeysResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";

    /// <summary>Tenant-wide maintenance: no single resource id to point at.</summary>
    public string? ResourceId => null;
}

/// <summary>Identifier of the queued Hangfire job, so the caller can follow it in the dashboard.</summary>
public sealed record BackfillPhoneticKeysResult(string JobId);
