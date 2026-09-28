using FluentValidation;

namespace Sankore.Modules.Customers.Features.Relationships.CloseRelationship;

public sealed class CloseRelationshipValidator : AbstractValidator<CloseRelationshipCommand>
{
    public CloseRelationshipValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.RelationshipId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(1000);
    }
}
