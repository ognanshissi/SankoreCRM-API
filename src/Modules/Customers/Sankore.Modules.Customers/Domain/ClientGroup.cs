namespace Sankore.Modules.Customers.Domain;

using Sankore.Modules.Customers.Domain.Events;
using Sankore.Shared.Kernel;

/// <summary>
/// Aggregate root for a collective client: solidarity group, tontine or VSLA.
/// <para>
/// A group is born <c>Forming</c> and only becomes <c>Active</c> once it reaches its minimum
/// size AND has a president, a treasurer and a secretary — see <see cref="TryActivate"/>.
/// Memberships are never deleted: leaving sets <c>LeftAt</c> so the group's history survives.
/// </para>
/// </summary>
public sealed class ClientGroup : AggregateRoot
{
    /// <summary>The three offices a constituted group must fill before it can be activated.</summary>
    private static readonly GroupOfficeRole[] RequiredOffices =
    [
        GroupOfficeRole.President,
        GroupOfficeRole.Treasurer,
        GroupOfficeRole.Secretary,
    ];

    private readonly List<GroupMembership> _memberships = [];

    public Guid Id { get; private set; }
    public GroupType Type { get; private set; }
    public string Name { get; private set; } = default!;
    public Guid AgencyId { get; private set; }
    public DateOnly ConstitutionDate { get; private set; }
    public GroupStatus Status { get; private set; }
    public string? DissolutionReason { get; private set; }
    public DateTimeOffset? DissolvedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, mapped as the optimistic concurrency token.</summary>
    public uint Version { get; private set; }

    public IReadOnlyCollection<GroupMembership> Memberships => _memberships.AsReadOnly();

    public int ActiveMemberCount => _memberships.Count(m => m.IsActive);

    private ClientGroup() { } // EF Core

    public static ClientGroup Create(
        Guid tenantId,
        GroupType type,
        string name,
        Guid agencyId,
        DateOnly constitutionDate,
        Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Group name is required.", "ClientGroup.Name.Required");
        if (agencyId == Guid.Empty)
            throw new DomainException("An agency is required.", "ClientGroup.Agency.Required");

        var now = DateTimeOffset.UtcNow;

        var group = new ClientGroup
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Type = type,
            Name = name.Trim(),
            AgencyId = agencyId,
            ConstitutionDate = constitutionDate,
            Status = GroupStatus.Forming,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        group.RaiseDomainEvent(new GroupCreatedDomainEvent(group.Id));
        return group;
    }

    /// <summary>
    /// Adds a member. Size and eligibility rules that need the tenant settings or the database
    /// (<see cref="CustomerErrors.GroupSizeLimitReached"/>,
    /// <see cref="CustomerErrors.AlreadyInSolidarityGroup"/>) are enforced by the handler.
    /// </summary>
    public GroupMembership AddMember(Guid clientId, GroupOfficeRole role, DateTimeOffset at, Guid actor)
    {
        if (Status == GroupStatus.Dissolved)
            throw new DomainException("Group is dissolved.", "ClientGroup.Dissolved");
        if (clientId == Guid.Empty)
            throw new DomainException("A client is required.", "GroupMembership.Client.Required");
        if (_memberships.Any(m => m.IsActive && m.ClientId == clientId))
            throw new DomainException("Client is already a member of this group.", "GroupMembership.AlreadyMember");

        if (role != GroupOfficeRole.Member)
            DemoteCurrentHolder(role);

        var membership = GroupMembership.Create(TenantId, Id, clientId, role, at, actor);
        _memberships.Add(membership);
        UpdatedAt = at;

        RaiseDomainEvent(new GroupMembershipChangedDomainEvent(Id, clientId, "Added"));
        return membership;
    }

    /// <summary>Closes an active membership — the row stays for history, only <c>LeftAt</c> is set.</summary>
    public Result RemoveMember(Guid clientId, string reason, DateTimeOffset at, Guid actor)
    {
        if (Status == GroupStatus.Dissolved)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        var membership = _memberships.FirstOrDefault(m => m.IsActive && m.ClientId == clientId);
        if (membership is null)
            return Result.Fail(CustomerErrors.MembershipNotFound);

        membership.Leave(reason, at);
        UpdatedAt = at;
        _ = actor;

        RaiseDomainEvent(new GroupMembershipChangedDomainEvent(Id, clientId, "Removed"));
        return Result.Ok();
    }

    /// <summary>Gives an office to a member; the previous holder of that office becomes a plain member.</summary>
    public Result AssignOfficeRole(Guid clientId, GroupOfficeRole role, Guid actor)
    {
        if (Status == GroupStatus.Dissolved)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        var membership = _memberships.FirstOrDefault(m => m.IsActive && m.ClientId == clientId);
        if (membership is null)
            return Result.Fail(CustomerErrors.MembershipNotFound);

        if (role != GroupOfficeRole.Member)
            DemoteCurrentHolder(role, exceptClientId: clientId);

        membership.SetRole(role);
        UpdatedAt = DateTimeOffset.UtcNow;
        _ = actor;

        RaiseDomainEvent(new GroupMembershipChangedDomainEvent(Id, clientId, "RoleChanged"));
        return Result.Ok();
    }

    /// <summary>
    /// Activates the group when it is complete. Deliberately forgiving: a group that is not
    /// complete yet simply stays <c>Forming</c> and the call still succeeds, so the create and
    /// add-member handlers can call it unconditionally. Only a group that is not
    /// <c>Forming</c> at all is rejected.
    /// </summary>
    public Result TryActivate(int minSize)
    {
        if (Status != GroupStatus.Forming)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        if (ActiveMemberCount < minSize)
            return Result.Ok();

        foreach (var office in RequiredOffices)
        {
            if (!_memberships.Any(m => m.IsActive && m.OfficeRole == office))
                return Result.Ok();
        }

        Status = GroupStatus.Active;
        UpdatedAt = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    public Result Suspend(string reason, Guid actor)
    {
        if (Status == GroupStatus.Dissolved)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);
        if (Status == GroupStatus.Suspended)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        Status = GroupStatus.Suspended;
        UpdatedAt = DateTimeOffset.UtcNow;
        _ = actor;
        return Result.Ok();
    }

    /// <summary>Terminal state: closes every active membership, then marks the group dissolved.</summary>
    public Result Dissolve(string reason, DateTimeOffset at, Guid actor)
    {
        if (Status == GroupStatus.Dissolved)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        foreach (var membership in _memberships.Where(m => m.IsActive).ToList())
            membership.Leave(reason, at);

        Status = GroupStatus.Dissolved;
        DissolutionReason = reason.Trim();
        DissolvedAt = at;
        UpdatedAt = at;
        _ = actor;

        RaiseDomainEvent(new GroupDissolvedDomainEvent(Id));
        return Result.Ok();
    }

    /// <summary>Re-parents a membership coming from an absorbed client (merge execution).</summary>
    internal void AttachMembership(GroupMembership m)
    {
        ArgumentNullException.ThrowIfNull(m);
        _memberships.Add(m);
    }

    private void DemoteCurrentHolder(GroupOfficeRole role, Guid? exceptClientId = null)
    {
        var holders = _memberships
            .Where(m => m.IsActive && m.OfficeRole == role && m.ClientId != exceptClientId)
            .ToList();

        foreach (var holder in holders)
            holder.SetRole(GroupOfficeRole.Member);
    }
}
