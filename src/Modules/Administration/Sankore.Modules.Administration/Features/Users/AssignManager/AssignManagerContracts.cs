using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignManager;

/// <summary>Sets who a user reports to. Idempotent: re-sending the same manager changes nothing.</summary>
public sealed record AssignManagerCommand(Guid UserId, Guid ManagerUserId)
    : IRequest<Result<AssignManagerResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}

/// <summary>Puts a user at the top of their reporting line. Idempotent.</summary>
public sealed record ClearManagerCommand(Guid UserId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}

public sealed record AssignManagerResult(
    Guid UserId,
    Guid ManagerUserId,
    string ManagerFullName,
    Guid? PreviousManagerUserId,
    bool Changed);

public static class AssignManagerErrors
{
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string ManagerNotFound = "MANAGER_NOT_FOUND";
    public const string ManagerNotActive = "MANAGER_NOT_ACTIVE";
    public const string SelfReportForbidden = "SELF_REPORT_FORBIDDEN";
    public const string SystemAccountImmutable = "SYSTEM_ACCOUNT_IMMUTABLE";

    /// <summary>
    /// The proposed manager already reports, directly or not, to the user being assigned.
    /// Accepting it would close the loop and make any walk of the hierarchy — an org chart, a
    /// approval escalation, a "my team" query — run forever.
    /// </summary>
    public const string ReportingCycle = "REPORTING_CYCLE";
}
