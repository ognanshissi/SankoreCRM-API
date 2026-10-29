using FluentValidation;
using Microsoft.Extensions.Options;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Actions.Executors;
using Sankore.Shared.Infrastructure.Egress;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;

internal sealed class AddActionValidator : AbstractValidator<AddActionCommand>
{
    public AddActionValidator(IOptions<OutboundHttpOptions> egress)
    {
        RuleFor(x => x.ConfigJson).NotEmpty();
        RuleFor(x => x.ExecutionOrder).GreaterThanOrEqualTo(0);

        RuleFor(x => x.ConfigJson)
            .Custom((json, ctx) =>
            {
                var cfg = CallWebhookConfig.Parse(json);
                var problem = cfg is null ? "La configuration du webhook n’est pas un JSON valide." : cfg.Validate(egress.Value);
                if (problem is not null) ctx.AddFailure(nameof(AddActionCommand.ConfigJson), problem);
            })
            .When(x => x.ActionType == ActionType.CallWebhook);
    }
}
