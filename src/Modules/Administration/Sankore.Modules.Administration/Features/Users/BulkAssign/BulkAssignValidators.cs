using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

/// <summary>
/// A selection has to be bounded: these handlers walk the list user by user because each one
/// needs its own eligibility decision and its own Identity write, so an unbounded list would
/// hold a request — and a transaction — open for as long as someone cares to select rows.
/// </summary>
internal static class BulkSelection
{
    public const int MaxUsers = 200;
}

public sealed class BulkAssignAgencyValidator : AbstractValidator<BulkAssignAgencyCommand>
{
    public BulkAssignAgencyValidator()
    {
        RuleFor(x => x.AgencyId).NotEqual(Guid.Empty);
        RuleFor(x => x.UserIds).NotEmpty().Must(ids => ids.Count <= BulkSelection.MaxUsers)
            .WithMessage($"At most {BulkSelection.MaxUsers} users can be assigned in one request.");
        RuleForEach(x => x.UserIds).NotEqual(Guid.Empty);
    }
}

public sealed class BulkAssignRoleValidator : AbstractValidator<BulkAssignRoleCommand>
{
    public BulkAssignRoleValidator()
    {
        RuleFor(x => x.RoleId).NotEqual(Guid.Empty);
        RuleFor(x => x.UserIds).NotEmpty().Must(ids => ids.Count <= BulkSelection.MaxUsers)
            .WithMessage($"At most {BulkSelection.MaxUsers} users can be assigned in one request.");
        RuleForEach(x => x.UserIds).NotEqual(Guid.Empty);
    }
}
