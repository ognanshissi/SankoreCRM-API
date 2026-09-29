namespace Sankore.Modules.Administration.Features.Users.GetLoginHistory;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetLoginHistoryQuery(
    Guid UserId,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<IReadOnlyList<LoginHistoryDto>>>;

public sealed record LoginHistoryDto(
    Guid Id,
    DateTimeOffset OccuredAt,
    double? Latitude,
    double? Longitude,
    string? IpAddress,
    /// <summary>Raw header, kept so an investigator can re-read what the parser interpreted.</summary>
    string? UserAgent,
    string? Browser,
    string? BrowserVersion,
    /// <summary>Windows, MacOs, Linux, Android, Ios, ChromeOs — or Unknown, never guessed.</summary>
    string Platform,
    /// <summary>Web or Mobile — or Unknown when the User-Agent could not be read.</summary>
    string ClientKind);
