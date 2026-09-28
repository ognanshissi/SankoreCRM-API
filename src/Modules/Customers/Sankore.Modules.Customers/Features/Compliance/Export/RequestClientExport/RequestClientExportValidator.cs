namespace Sankore.Modules.Customers.Features.Compliance.Export.RequestClientExport;

using FluentValidation;

public sealed class RequestClientExportValidator : AbstractValidator<RequestClientExportCommand>
{
    public RequestClientExportValidator()
    {
        RuleFor(x => x.Filters).NotNull();

        When(x => x.Filters is not null, () =>
        {
            RuleFor(x => x.Filters.ClientNumber).MaximumLength(50);
            RuleFor(x => x.Filters.Name).MaximumLength(200);
            RuleFor(x => x.Filters.Phone).MaximumLength(30);
            RuleFor(x => x.Filters.IdentityDocumentNumber).MaximumLength(50);
            RuleFor(x => x.Filters.SegmentCode).MaximumLength(50);
            RuleFor(x => x.Filters.Status).IsInEnum().When(x => x.Filters.Status.HasValue);
            RuleFor(x => x.Filters.Type).IsInEnum().When(x => x.Filters.Type.HasValue);
        });
    }
}
