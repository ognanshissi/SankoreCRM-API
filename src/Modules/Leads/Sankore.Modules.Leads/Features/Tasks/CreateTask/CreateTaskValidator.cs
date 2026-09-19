namespace Sankore.Modules.Leads.Features.Tasks.CreateTask;

using FluentValidation;

internal sealed class CreateTaskValidator : AbstractValidator<CreateTaskCommand>
{
    public CreateTaskValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DueAt).GreaterThan(DateTimeOffset.UtcNow);
        RuleFor(x => x.TenantId).NotEmpty();
    }
}
