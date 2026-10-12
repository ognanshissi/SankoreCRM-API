using FluentValidation;

namespace Sankore.Modules.Workflow.Features.Templates.AddStep;

public sealed class AddStepValidator : AbstractValidator<AddStepCommand>
{
    public AddStepValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.Order).GreaterThanOrEqualTo(1);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.ApproverRoleCode).MaximumLength(100);
        RuleFor(x => x.TimeoutHours).GreaterThan(0).When(x => x.TimeoutHours.HasValue);
        RuleFor(x => x.ConfigJson).MaximumLength(8000)
            .Must(BeJsonOrEmpty).WithMessage("ConfigJson must be a JSON object.");
    }

    private static bool BeJsonOrEmpty(string? json)
    {
        if (string.IsNullOrEmpty(json)) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
