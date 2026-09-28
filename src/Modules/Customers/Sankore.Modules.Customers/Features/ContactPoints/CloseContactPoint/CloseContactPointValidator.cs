using FluentValidation;

namespace Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;

public sealed class CloseContactPointValidator : AbstractValidator<CloseContactPointCommand>
{
    public CloseContactPointValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.ContactPointId).NotEmpty();
    }
}
