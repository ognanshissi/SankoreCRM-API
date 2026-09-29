namespace Sankore.Modules.Notifications.Features.TestSend;

using FluentValidation;

internal sealed class SendTestEmailValidator : AbstractValidator<SendTestEmailCommand>
{
    public SendTestEmailValidator()
    {
        RuleFor(x => x.RecipientEmail).NotEmpty().EmailAddress();
    }
}
