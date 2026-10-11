namespace Sankore.Modules.Customers.Features.Compliance.ProfileNotices;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records that the institution has LEARNED of a change to a client's profile, starting the CIMA
/// LBC/FT fifteen-day clock.
///
/// <para>
/// <see cref="NoticedOn"/> is caller-supplied — an agent records on Monday what a client told them
/// on Friday — while the RECORDER is taken from <c>ICurrentUser</c> and never from the command,
/// the rule this solution applies to every audit actor.
/// </para>
/// </summary>
internal sealed record RecordProfileNoticeCommand(
    Guid ClientId,
    ProfileChangeKind Kind,
    DateOnly NoticedOn,
    string? Note
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

internal sealed record RecordProfileNoticeRequest(
    ProfileChangeKind Kind,
    DateOnly NoticedOn,
    string? Note = null);

/// <summary>Closes a notice because the change HAS been carried into the client record.</summary>
internal sealed record IntegrateProfileNoticeCommand(Guid NoticeId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientProfileChangeNotice";
    public string? ResourceId => NoticeId.ToString();
}

/// <summary>Closes a notice because no update turned out to be needed.</summary>
internal sealed record DismissProfileNoticeCommand(Guid NoticeId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientProfileChangeNotice";
    public string? ResourceId => NoticeId.ToString();
}

internal sealed record DismissProfileNoticeRequest(string Reason);

/// <summary>
/// The notices an institution is late on. A REPORT and not a gate: by the time a notice is
/// overdue the breach has happened, and refusing the very update that would close it would be
/// perverse.
/// </summary>
internal sealed record OverdueProfileNoticesQuery(int Page, int PageSize)
    : IRequest<Result<PagedResult<OverdueProfileNoticeDto>>>;

/// <param name="ElapsedDays">
/// Days since the institution learned of the change. Always greater than the allowance here, by
/// construction.
/// </param>
/// <param name="AllowedDays">
/// The tenant's configured deadline, carried so a screen does not hard-code fifteen.
/// </param>
public sealed record OverdueProfileNoticeDto(
    Guid NoticeId,
    Guid ClientId,
    string ClientNumber,
    string Kind,
    DateOnly NoticedOn,
    int ElapsedDays,
    int AllowedDays,
    Guid RecordedBy);
