namespace Sankore.Modules.Customers.Features.Groups.Shared;

/// <summary>
/// One membership of a group. Carries no sensitive value: the client's phone,
/// document number and date of birth stay behind the audited reveal endpoint.
/// <paramref name="LeftAt"/> is null for an active member.
/// </summary>
public sealed record GroupMemberDto(
    Guid MembershipId,
    Guid ClientId,
    string ClientNumber,
    string DisplayName,
    string ClientStatus,
    string OfficeRole,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt,
    string? LeaveReason);
