namespace Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;

using System.Diagnostics;
using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-11. Three things happen, in this order: the rolling-hour quota is checked,
/// the ONE requested field is decrypted, and the access is logged.
///
/// Only the requested field is decrypted — never the whole record — so an operator who
/// needs a phone number does not incidentally pull a document number into a process's
/// memory, and the access log says precisely which field was seen.
///
/// The log row is written in the SAME transaction as the response is produced, so there
/// is no window in which a value was revealed without a trace.
/// </summary>
internal sealed class RevealSensitiveFieldHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    ICustomerSettings settings,
    IFieldEncryptor encryptor,
    INotificationsModule notifications,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    ILogger<RevealSensitiveFieldHandler> logger
) : IRequestHandler<RevealSensitiveFieldCommand, Result<RevealSensitiveFieldResult>>
{
    /// <summary>
    /// Tenant setting holding the compliance officer's mailbox. Not part of
    /// <c>CustomerSettingKeys.Defaults</c> on purpose: there is no sensible factory
    /// default for someone's e-mail address, so its absence is a normal state and simply
    /// means "log the alert, send no mail".
    /// </summary>
    private const string ComplianceAlertEmailKey = "compliance-alert-email";

    private const string ThresholdExceededTemplate = "customers.reveal-threshold-exceeded";

    public async Task<Result<RevealSensitiveFieldResult>> Handle(
        RevealSensitiveFieldCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;
        var now = DateTimeOffset.UtcNow;

        var client = await db.Clients
            .Include(c => c.ContactPoints)
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail<RevealSensitiveFieldResult>(CustomerErrors.ClientNotFound);

        if (!await agencyScope.CanAccessAgencyAsync(tenantId, actor, client.AgencyId, ct))
            return Result.Fail<RevealSensitiveFieldResult>(CustomerErrors.ClientNotFound);

        // ── 1. Rolling-hour quota, per USER and not per client ───────────────
        // Per user is what catches the real abuse pattern: harvesting one field from many
        // clients. A per-client counter would let an operator walk the whole base.
        var limit = await settings.GetIntAsync(tenantId, CustomerSettingKeys.RevealLimitPerHour, ct);
        var windowStart = now.AddHours(-1);

        var revealsInWindow = await db.SensitiveDataAccessLogs
            .CountAsync(l => l.ActorUserId == actor && l.AccessedAt >= windowStart, ct);

        if (revealsInWindow >= limit)
        {
            await RaiseThresholdAlertAsync(tenantId, actor, revealsInWindow, limit, ct);
            return Result.Fail<RevealSensitiveFieldResult>(CustomerErrors.RevealRateLimitExceeded);
        }

        // ── 2. Decrypt the requested field, and only that one ────────────────
        var ciphertext = ResolveCiphertext(client, cmd);
        if (string.IsNullOrWhiteSpace(ciphertext))
            return Result.Fail<RevealSensitiveFieldResult>(CustomerErrors.UnknownSensitiveField);

        string? clear;
        try
        {
            clear = encryptor.Decrypt(ciphertext);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException
                                      or System.Security.Cryptography.CryptographicException
                                      or ArgumentException)
        {
            // An unreadable envelope is an infrastructure problem (rotated key, data copied
            // between environments), but the caller must not learn anything about the
            // stored bytes — it reads as "this field has no revealable value".
            logger.LogError(ex,
                "Failed to decrypt {Field} of client {ClientId} in tenant {TenantId}.",
                cmd.Field, client.Id, tenantId);

            return Result.Fail<RevealSensitiveFieldResult>(CustomerErrors.UnknownSensitiveField);
        }

        if (string.IsNullOrWhiteSpace(clear))
            return Result.Fail<RevealSensitiveFieldResult>(CustomerErrors.UnknownSensitiveField);

        // ── 3. Log the access before the value is handed over ────────────────
        db.SensitiveDataAccessLogs.Add(SensitiveDataAccessLog.Record(
            tenantId: tenantId,
            clientId: client.Id,
            actorUserId: actor,
            fieldName: cmd.Field.ToString(),
            accessedAt: now,
            // The distributed trace id ties this reveal to the HTTP request (and to the
            // other reveals of the same screen) in Seq without storing anything personal.
            correlationId: Activity.Current?.Id));

        await db.SaveChangesAsync(ct);

        return Result.Ok(new RevealSensitiveFieldResult(
            Field: cmd.Field.ToString(),
            Value: clear,
            RevealedAt: now));
    }

    /// <summary>
    /// Maps a <see cref="SensitiveField"/> onto the column (or contact point) that holds it.
    /// Returns null when this client has nothing stored for that field, which the caller
    /// turns into <c>UNKNOWN_SENSITIVE_FIELD</c>.
    /// </summary>
    private static string? ResolveCiphertext(Client client, RevealSensitiveFieldCommand cmd) => cmd.Field switch
    {
        SensitiveField.IdentityDocumentNumber => client.EncryptedIdentityDocumentNumber,
        SensitiveField.DateOfBirth => client.EncryptedDateOfBirth,
        SensitiveField.DeclaredIncome => client.EncryptedDeclaredIncome,
        SensitiveField.RegistrationNumber => client.EncryptedRegistrationNumber,
        SensitiveField.TaxIdNumber => client.EncryptedTaxIdNumber,

        SensitiveField.Phone => ResolveContactPoint(client, ContactPointType.Phone, cmd.ContactPointId),
        SensitiveField.Email => ResolveContactPoint(client, ContactPointType.Email, cmd.ContactPointId),
        SensitiveField.PostalAddress => ResolveContactPoint(client, ContactPointType.Address, cmd.ContactPointId),

        _ => null
    };

    /// <summary>
    /// The named contact point when an id was given (and it matches the requested type and
    /// is still open), otherwise the active primary one of that type.
    /// </summary>
    private static string? ResolveContactPoint(Client client, ContactPointType type, Guid? contactPointId)
    {
        var candidates = client.ContactPoints.Where(cp => cp.IsActive && cp.Type == type);

        var contactPoint = contactPointId.HasValue
            ? candidates.FirstOrDefault(cp => cp.Id == contactPointId.Value)
            : candidates.OrderByDescending(cp => cp.IsPrimary)
                        .ThenByDescending(cp => cp.ValidFrom)
                        .FirstOrDefault();

        return contactPoint?.EncryptedValue;
    }

    /// <summary>
    /// Emits the security signal for a user who went over the quota: an integration event
    /// for whoever watches the outbox, plus a mail to the compliance officer when the
    /// tenant configured one.
    ///
    /// Both writes happen in a SUPPRESSED transaction scope. The handler is about to
    /// return <c>Result.Fail</c>, and <c>TransactionBehavior</c> rolls the ambient scope
    /// back on failure — which would discard the very alert that the refusal exists to
    /// raise. Suppressing gives the alert its own short-lived transaction, so the reveal is
    /// still denied while the trace of the attempt survives.
    /// </summary>
    private async Task RaiseThresholdAlertAsync(
        Guid tenantId, Guid actor, int revealsInWindow, int limit, CancellationToken ct)
    {
        logger.LogWarning(
            "User {ActorUserId} of tenant {TenantId} exceeded the sensitive-reveal quota "
            + "({RevealsInWindow} reveals in the last hour, limit {Limit}).",
            actor, tenantId, revealsInWindow, limit);

        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);

        try
        {
            await publisher.PublishAsync(
                new SensitiveRevealThresholdExceededEvent(
                    TenantId: tenantId,
                    ActorUserId: actor,
                    RevealsInWindow: revealsInWindow,
                    Threshold: limit),
                ct);

            await db.SaveChangesAsync(ct);

            var allSettings = await settings.GetAllAsync(tenantId, ct);
            if (allSettings.TryGetValue(ComplianceAlertEmailKey, out var complianceEmail)
                && !string.IsNullOrWhiteSpace(complianceEmail))
            {
                await notifications.QueueEmailAsync(new QueueEmailRequest(
                    TemplateKey: ThresholdExceededTemplate,
                    RecipientEmail: complianceEmail.Trim(),
                    RecipientName: null,
                    Module: "Customers",
                    Locale: "fr",
                    TemplateData: new Dictionary<string, object>
                    {
                        ["actorUserId"] = actor,
                        ["revealsInWindow"] = revealsInWindow,
                        ["threshold"] = limit,
                    },
                    // One mail per user per hour: the key deliberately has hour precision so
                    // a user hammering the endpoint does not flood the compliance mailbox.
                    IdempotencyKey: $"customers:reveal-threshold:{tenantId:D}:{actor:D}:{DateTimeOffset.UtcNow:yyyyMMddHH}",
                    TenantId: tenantId), ct);
            }
            else
            {
                logger.LogWarning(
                    "Tenant {TenantId} has no '{SettingKey}' configured: the reveal-quota alert "
                    + "was recorded but no e-mail was sent.",
                    tenantId, ComplianceAlertEmailKey);
            }

            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The alert is best-effort: failing to raise it must not change the answer the
            // caller gets, which is a refusal either way.
            logger.LogError(ex,
                "Could not raise the sensitive-reveal quota alert for user {ActorUserId} of tenant {TenantId}.",
                actor, tenantId);
        }
    }
}
