namespace Sankore.Shared.Kernel.Authorization;

public interface IRequiredAgencyAccess
{
    Guid AgencyId { get; }
    string PermissionRequired { get; }   // ex: "Validation", "LectureSeule", "Supervision"
}