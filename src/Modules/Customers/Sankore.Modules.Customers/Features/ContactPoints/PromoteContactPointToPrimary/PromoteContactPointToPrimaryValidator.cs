using FluentValidation;

namespace Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;

public sealed class PromoteContactPointToPrimaryValidator
    : AbstractValidator<PromoteContactPointToPrimaryCommand>
{
    public PromoteContactPointToPrimaryValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.ContactPointId).NotEmpty();
    }
}
