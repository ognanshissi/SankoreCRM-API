namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using FluentValidation;

internal sealed class SendSnippetValidator : AbstractValidator<SendSnippetCommand>
{
    public SendSnippetValidator()
    {
        RuleFor(x => x.SourceId).NotEmpty();
        RuleFor(x => x.RecipientEmail).NotEmpty().EmailAddress();
    }
}
