namespace Sankore.Modules.Customers.Features.Groups.CreateGroup;

using FluentValidation;

public sealed class CreateGroupValidator : AbstractValidator<CreateGroupCommand>
{
    public CreateGroupValidator()
    {
        RuleFor(x => x.Type).IsInEnum();

        // 150 = the column width of client_groups.name.
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(150);

        RuleFor(x => x.AgencyId).NotEmpty();

        // A group cannot be constituted in the future; "today" is evaluated in UTC
        // because that is the clock every server-side timestamp of this module uses.
        RuleFor(x => x.ConstitutionDate)
            .NotEqual(default(DateOnly))
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Group.ConstitutionDate.NotInFuture");
    }
}
