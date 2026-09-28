namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Membership of a client in a <see cref="ClientGroup"/>. Never deleted: leaving the group
/// sets <see cref="LeftAt"/> so a past composition can always be reconstructed.
/// </summary>
public sealed class GroupMembership
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid GroupId { get; private set; }
    public Guid ClientId { get; private set; }
    public GroupOfficeRole OfficeRole { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
    public DateTimeOffset? LeftAt { get; private set; }
    public string? LeaveReason { get; private set; }
    public Guid CreatedBy { get; private set; }

    public bool IsActive => LeftAt is null;

    private GroupMembership() { } // EF Core

    internal static GroupMembership Create(
        Guid tenantId,
        Guid groupId,
        Guid clientId,
        GroupOfficeRole officeRole,
        DateTimeOffset joinedAt,
        Guid createdBy)
    {
        if (clientId == Guid.Empty)
            throw new DomainException("A client is required.", "GroupMembership.Client.Required");

        return new GroupMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GroupId = groupId,
            ClientId = clientId,
            OfficeRole = officeRole,
            JoinedAt = joinedAt,
            CreatedBy = createdBy,
        };
    }

    internal void Leave(string reason, DateTimeOffset at)
    {
        if (LeftAt is not null)
            return;

        LeftAt = at;
        LeaveReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    internal void SetRole(GroupOfficeRole role) => OfficeRole = role;

    /// <summary>Re-parents the membership onto the surviving client of a merge.</summary>
    internal void ReassignTo(Guid clientId) => ClientId = clientId;
}
