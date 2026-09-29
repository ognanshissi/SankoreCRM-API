namespace Sankore.Modules.Notifications.Features.TestSend;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Senders;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class SendTestEmailHandler(
    IEmailProviderResolver resolver,
    IEmailSender sender,
    ICurrentUser currentUser,
    ITenantContext tenant,
    ILogger<SendTestEmailHandler> logger)
    : IRequestHandler<SendTestEmailCommand, Result<TestEmailResult>>
{
    public async Task<Result<TestEmailResult>> Handle(
        SendTestEmailCommand request, CancellationToken ct)
    {
        var tenantId = tenant.CurrentTenantId;
        var provider = await resolver.ResolveAsync(tenantId, ct);

        var usedTenantProvider = !provider.IsDefault;

        var message = new SendEmailRequest(
            MessageId: Guid.NewGuid(),
            TenantId: tenantId,
            FromEmail: provider.FromEmail ?? "noreply@sankore.io",
            FromName: provider.FromName ?? "Sankore",
            ReplyToEmail: provider.ReplyToEmail,
            ToEmail: request.RecipientEmail,
            ToName: currentUser.DisplayName,
            Subject: "Test de configuration — Sankore",
            HtmlBody: """
                <!DOCTYPE html>
                <html lang="fr"><body style="font-family:sans-serif;color:#111">
                  <h2>La configuration fonctionne</h2>
                  <p>Ce message a été envoyé depuis Sankore pour vérifier le paramétrage
                     d'envoi de votre institution. Aucune action n'est attendue.</p>
                </body></html>
                """,
            TextBody: "La configuration fonctionne. Ce message vérifie le paramétrage d'envoi de Sankore.");

        try
        {
            await sender.SendAsync(message, provider, ct);

            logger.LogInformation(
                "Test email delivered for tenant {TenantId} via {Provider}", tenantId, provider.ProviderType);

            return Result.Ok(new TestEmailResult(true, provider.ProviderType, usedTenantProvider, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Returned as a SUCCESSFUL result carrying the failure: the command did what it was
            // asked — it tested — and the caller needs the provider's message, not a 500.
            logger.LogWarning(ex,
                "Test email failed for tenant {TenantId} via {Provider}", tenantId, provider.ProviderType);

            return Result.Ok(new TestEmailResult(
                false, provider.ProviderType, usedTenantProvider, ex.Message));
        }
    }
}
