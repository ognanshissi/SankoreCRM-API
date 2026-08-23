namespace Sankore.Modules.Administration.Features.Agencies.GetAgencyTree;

public sealed record AgencyTreeNodeDto(
    Guid Id,
    string Name,
    string Code,
    string Description,
    string AgencyType,
    Guid? ParentAgencyId,
    bool IsHeadQuarterAgency,
    bool IsActive,
    int ChildCount,
    List<AgencyTreeNodeDto> Children);
