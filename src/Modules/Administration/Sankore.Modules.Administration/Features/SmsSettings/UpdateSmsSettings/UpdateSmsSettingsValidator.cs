namespace Sankore.Modules.Administration.Features.SmsSettings.UpdateSmsSettings;

using FluentValidation;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// What can be refused before anything is written.
///
/// <para>
/// Messages are in French and inline rather than through <c>IStringLocalizer</c>, which the email
/// validator next door uses: its keys live in <c>AdministrationErrors</c> resources, and adding
/// six more there for a screen that exists in one language would spread this feature across two
/// files for no reader's benefit. If this module's resources ever gain a second locale, these
/// move with the rest.
/// </para>
/// </summary>
internal sealed class UpdateSmsSettingsValidator : AbstractValidator<UpdateSmsSettingsCommand>
{
    /// <summary>
    /// Brevo's own ceiling on an alphanumeric sender. Enforced here rather than discovered at
    /// send time, where it surfaces as a dead-lettered message hours after the mistake.
    /// </summary>
    private const int BrevoSenderMaxLength = 11;

    /// <summary>A Twilio Account SID is 34 characters and starts with AC.</summary>
    private const int TwilioAccountSidLength = 34;

    public UpdateSmsSettingsValidator()
    {
        RuleFor(x => x.ProviderType)
            .NotEmpty()
            .Must(p => TenantSmsSettings.AllowedProviders.Contains(p))
            .WithMessage("Fournisseur SMS inconnu. Valeurs acceptées : Disabled, Brevo, Twilio.");

        // Everything below applies only to a channel that is being turned ON. Disabling must stay
        // possible with no sender and no credential — otherwise a tenant whose provider dropped
        // them could not switch the channel off without first repairing it.
        When(x => x.ProviderType != TenantSmsSettings.DisabledProvider, () =>
        {
            RuleFor(x => x.SenderId)
                .NotEmpty()
                .WithMessage("Un identifiant d'expéditeur est obligatoire : les deux "
                           + "fournisseurs refusent un message qui n'en porte pas.");

            // Required to ENABLE, not to disable. Without it a number stored as « 0708091801 »
            // cannot be turned into an E.164 address, so most of a tenant's customers would be
            // silently unreachable — a failure much cheaper to learn here than at send time.
            RuleFor(x => x.DefaultCallingCode)
                .NotEmpty()
                .WithMessage("L'indicatif pays est obligatoire : les numéros enregistrés au "
                           + "format local ne peuvent pas être composés sans lui.")
                .Must(c => c is null || c.Where(char.IsAsciiDigit).Count() is >= 1 and <= 3)
                .WithMessage("Un indicatif pays compte 1 à 3 chiffres (225, 221, 33…).");
        });

        When(x => x.ProviderType == "Brevo", () =>
        {
            RuleFor(x => x.SenderId)
                .MaximumLength(BrevoSenderMaxLength)
                .When(x => !string.IsNullOrWhiteSpace(x.SenderId))
                .WithMessage($"Brevo limite le nom d'expéditeur à {BrevoSenderMaxLength} "
                           + "caractères.");

            // Letters and digits only. Brevo refuses accents, spaces and punctuation in an
            // alphanumeric sender, and the refusal arrives as a rejected message rather than as a
            // configuration error.
            RuleFor(x => x.SenderId)
                .Matches("^[A-Za-z0-9]+$")
                .When(x => !string.IsNullOrWhiteSpace(x.SenderId))
                .WithMessage("Le nom d'expéditeur Brevo n'accepte que des lettres non accentuées "
                           + "et des chiffres, sans espace.");
        });

        When(x => x.ProviderType == "Twilio", () =>
        {
            RuleFor(x => x.AccountSid)
                .NotEmpty()
                .WithMessage("Le SID de compte Twilio est obligatoire.")
                .Length(TwilioAccountSidLength)
                .When(x => !string.IsNullOrWhiteSpace(x.AccountSid))
                .WithMessage($"Un SID de compte Twilio fait {TwilioAccountSidLength} caractères.");

            // A Twilio sender is a number it issued OR a messaging-service SID — the two shapes
            // this accepts, and nothing else. An arbitrary string here is a message Twilio
            // rejects.
            RuleFor(x => x.SenderId)
                .Must(IsTwilioSender)
                .When(x => !string.IsNullOrWhiteSpace(x.SenderId))
                .WithMessage("L'expéditeur Twilio doit être un numéro au format international "
                           + "(+225…) ou le SID d'un Messaging Service (MG…).");
        });
    }

    private static bool IsTwilioSender(string? senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId)) return false;

        var value = senderId.Trim();

        // E.164: a plus, then 1 to 15 digits.
        if (value.StartsWith('+')
            && value.Length is >= 8 and <= 16
            && value[1..].All(char.IsAsciiDigit))
        {
            return true;
        }

        // A Messaging Service SID: MG followed by 32 hex characters.
        return value.Length == TwilioAccountSidLength
            && value.StartsWith("MG", StringComparison.Ordinal)
            && value[2..].All(char.IsAsciiHexDigit);
    }
}
