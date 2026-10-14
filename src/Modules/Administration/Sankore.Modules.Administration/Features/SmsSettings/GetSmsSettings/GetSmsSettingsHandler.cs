namespace Sankore.Modules.Administration.Features.SmsSettings.GetSmsSettings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class GetSmsSettingsHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetSmsSettingsQuery, Result<SmsSettingsDto>>
{
    public async Task<Result<SmsSettingsDto>> Handle(
        GetSmsSettingsQuery request, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant.
        var settings = await db.TenantSmsSettings.FirstOrDefaultAsync(ct);

        // A tenant that never opened this screen gets the shape of an unconfigured channel rather
        // than a 404: the screen is a form, and a form needs its options and its hints whether or
        // not anything has been saved.
        var providerType = settings?.ProviderType ?? TenantSmsSettings.DisabledProvider;

        return Result.Ok(new SmsSettingsDto(
            ProviderType: providerType,
            SenderId: settings?.SenderId,
            AccountSid: settings?.AccountSid,
            DefaultCallingCode: settings?.DefaultCallingCode,
            HasCredential: settings?.HasCredential ?? false,
            CanSend: settings?.CanSend ?? false,
            MonthlyQuotaLimit: settings?.MonthlyQuotaLimit,
            CurrentMonthUsageCount: settings?.CurrentMonthUsageCount ?? 0,
            RequiresAccountSid: TenantSmsSettings.UsesAccountSid(providerType),
            SenderIdHint: SenderIdHint(providerType),
            AvailableProviders: TenantSmsSettings.AllowedProviders));
    }

    /// <summary>
    /// What the chosen provider expects as a sender identity, said in the words an administrator
    /// can act on.
    ///
    /// <para>
    /// Server-side because the rule is the provider's: Brevo rejects an alphanumeric sender over
    /// eleven characters, Twilio rejects anything that is not a number it issued. A screen that
    /// hard-coded either would be wrong for the other, and silently wrong — the message is
    /// refused at send time, hours later, in a dead-letter row.
    /// </para>
    /// </summary>
    private static string? SenderIdHint(string providerType) => providerType switch
    {
        "Brevo" => "Nom d'expéditeur alphanumérique, 11 caractères maximum (ex. SANKORE). "
                 + "Doit être déclaré et approuvé dans votre compte Brevo.",

        "Twilio" => "Un numéro que Twilio vous a attribué, au format international "
                  + "(ex. +22520123456), ou le SID d'un Messaging Service.",

        _ => null,
    };
}
