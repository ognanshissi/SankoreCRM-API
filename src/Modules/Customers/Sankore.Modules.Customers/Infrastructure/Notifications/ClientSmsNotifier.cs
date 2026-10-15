namespace Sankore.Modules.Customers.Infrastructure.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// The one place in this solution that turns a client id into a sent text message.
///
/// <para>
/// The twin of <see cref="ClientEmailNotifier"/> and for the identical reason: the client's
/// telephone number is M01's secret — AES-256-GCM in <c>client_contact_points</c> under this
/// module's key — so other modules ask for a SEND and never for a number. M02 needs it for the
/// remote-KYC second factor (KRM-B-12), and handing it the number would copy a regulated field
/// into a module with no key for it.
/// </para>
///
/// <para>
/// <b>Where it does real work the email notifier does not: turning a stored number into a
/// dialable address.</b> M01 keeps numbers as they were dialled —
/// <c>SensitiveValueNormalizer.NormalizePhone</c> deliberately does not infer an indicatif,
/// because inferring one wrongly makes two different subscribers collide in a blind index — so a
/// customer captured at a counter in Abidjan is <c>0708091801</c> and a provider needs
/// <c>+2250708091801</c>. The tenant's configured calling code is what bridges the two, and a
/// tenant that has not configured one has no SMS channel at all.
/// </para>
/// </summary>
// Public for the reason its neighbour is: the facade is internal, and public consumers and test
// doubles reference this type.
public sealed class ClientSmsNotifier(
    CustomersDbContext db,
    IFieldEncryptor encryptor,
    INotificationsModule notifications,
    IAdministrationModule administration,
    ITenantStore tenantStore,
    ILogger<ClientSmsNotifier> logger)
{
    /// <summary>
    /// Whether the client has an active telephone number on file — a boolean, never the number.
    /// Does NOT decrypt, for the reason <c>HasEmailAsync</c> does not: a gate deciding whether to
    /// offer a second factor must not depend on the field-encryption key being right.
    /// </summary>
    public Task<bool> HasPhoneAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => db.ClientContactPoints
            .IgnoreQueryFilters()
            .AnyAsync(
                cp => cp.TenantId == tenantId
                   && cp.ClientId == clientId
                   && cp.Type == ContactPointType.Phone
                   && cp.ValidTo == null,
                ct);

    /// <summary>
    /// The client's active telephone number masked, or null when there is none — the projection an
    /// agent's send dialog shows before a link goes out by SMS.
    /// </summary>
    /// <remarks>
    /// The same <c>SensitiveValueMasker.MaskPhone</c> M01's own contact-point list already applies,
    /// so this reveals nothing a screen in this product does not already show. It DECRYPTS, which
    /// is why <see cref="HasPhoneAsync"/> exists separately for the gate — exactly the split the
    /// email path makes, and for the same reason.
    ///
    /// <para>
    /// Masked from the STORED value rather than from the E.164 form: the agent recognises the
    /// number they typed into the file, and showing them a +225 prefix they never entered reads
    /// as somebody else's number.
    /// </para>
    /// </remarks>
    public async Task<string?> GetMaskedPhoneAsync(
        Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var encrypted = await db.ClientContactPoints
            .IgnoreQueryFilters()
            .Where(cp => cp.TenantId == tenantId
                      && cp.ClientId == clientId
                      && cp.Type == ContactPointType.Phone
                      && cp.ValidTo == null)
            .OrderByDescending(cp => cp.IsPrimary)
            .Select(cp => cp.EncryptedValue)
            .FirstOrDefaultAsync(ct);

        if (encrypted is null) return null;

        try
        {
            return SensitiveValueMasker.MaskPhone(encryptor.Decrypt(encrypted));
        }
        catch (Exception ex)
        {
            // A KEY problem, not a missing number. Null rather than throwing: this feeds a
            // confirmation dialog, and a screen that cannot name the destination must still open.
            logger.LogError(ex,
                "Could not decrypt the telephone of client {ClientId} for display.", clientId);
            return null;
        }
    }

    /// <summary>
    /// Queues one templated text message to a client. Answers whether it was queued, and never
    /// throws — every caller is a consumer, a job or an anonymous endpoint whose own work must
    /// stand whether or not the message went out.
    /// </summary>
    public async Task<bool> NotifyAsync(NotifyClientBySmsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var client = await db.Clients
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == request.TenantId && c.Id == request.ClientId)
                .Select(c => new { c.DisplayName, c.FirstName, c.PreferredLanguage })
                .FirstOrDefaultAsync(ct);

            if (client is null)
            {
                logger.LogWarning(
                    "No SMS sent for template {TemplateKey}: client {ClientId} does not exist.",
                    request.TemplateKey, request.ClientId);
                return false;
            }

            // The tenant's calling code, needed to turn a local number into E.164. Read from the
            // SMS settings rather than from the company record because nothing else holds it —
            // see TenantSmsSettings.DefaultCallingCode.
            var sms = await administration.GetSmsConfigAsync(request.TenantId, ct);

            if (sms is null || !sms.CanSend)
            {
                // Ordinary rather than an incident: most tenants have no SMS channel. Logged at
                // information so a caller investigating "no code arrived" can see it was never
                // queued, rather than looking for a provider error that does not exist.
                logger.LogInformation(
                    "No SMS sent for template {TemplateKey}: tenant {TenantId} has no usable SMS channel.",
                    request.TemplateKey, request.TenantId);
                return false;
            }

            var phone = await ResolveE164Async(
                request.TenantId, request.ClientId, sms.DefaultCallingCode, ct);

            if (phone is null)
            {
                logger.LogInformation(
                    "No SMS sent for template {TemplateKey}: client {ClientId} has no usable "
                    + "telephone number.",
                    request.TemplateKey, request.ClientId);
                return false;
            }

            var tenant = await tenantStore.GetAsync(request.TenantId, ct);

            // The client's own preference, then the tenant's default, then French. The fallback
            // lives here and nowhere else, exactly as on the email path.
            var locale = client.PreferredLanguage ?? tenant?.DefaultLanguage ?? "fr";

            var data = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var pair in request.TemplateData ?? new Dictionary<string, object>())
                data[pair.Key] = pair.Value;

            // Filled AFTER the caller's values, so a caller cannot address somebody else's
            // customer by name. Deliberately fewer than the email path fills: a text message sits
            // unlocked on a lock screen, so the templates are written to need as little identity
            // as possible — see SmsSeeder.
            data["client_first_name"] = FirstNameOrFallback(client.FirstName, client.DisplayName);
            data["company_name"] = tenant?.Name ?? string.Empty;

            var queued = await notifications.QueueSmsAsync(new QueueSmsRequest(
                TemplateKey: request.TemplateKey,
                RecipientPhone: phone,
                Module: "Customers",
                Locale: locale,
                TemplateData: data,
                IdempotencyKey: request.IdempotencyKey,
                TenantId: request.TenantId), ct);

            if (queued.IsFailure)
            {
                // DUPLICATE_IDEMPOTENCY_KEY lands here and is not a failure worth alarming about:
                // it is the mechanism doing its job on a retried consumer. Logged at information
                // for that one, warning for the rest.
                if (queued.Error == "DUPLICATE_IDEMPOTENCY_KEY")
                {
                    logger.LogInformation(
                        "SMS {TemplateKey} for client {ClientId} was already queued; not sent twice.",
                        request.TemplateKey, request.ClientId);

                    // TRUE: the caller asked for the customer to receive this message, and they
                    // have one queued. Reporting false would make a retried consumer conclude the
                    // send failed and try another route.
                    return true;
                }

                logger.LogWarning(
                    "Could not queue SMS {TemplateKey} for client {ClientId}: {Error}",
                    request.TemplateKey, request.ClientId, queued.Error);

                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Sending SMS {TemplateKey} to client {ClientId} threw.",
                request.TemplateKey, request.ClientId);
            return false;
        }
    }

    /// <summary>
    /// The client's active telephone number as an E.164 address, or null.
    ///
    /// <para>
    /// Prefers the one marked primary and falls back to any active one — the same rule as the
    /// email path, and the same single decryption point.
    /// </para>
    ///
    /// <para>
    /// <b>The conversion to E.164 is where this method earns its existence.</b> A stored value is
    /// whatever was dialled: <c>+2250708091801</c>, <c>00225 07 08 09 18 01</c> or
    /// <c>0708091801</c>. The first two already carry their country; the third does not, and the
    /// tenant's configured code is prefixed to it. A number that still cannot be made into a
    /// plausible address is refused rather than guessed at — sending a one-time code to a stranger
    /// is worse than not sending it.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveE164Async(
        Guid tenantId, Guid clientId, string? defaultCallingCode, CancellationToken ct)
    {
        var encrypted = await db.ClientContactPoints
            .IgnoreQueryFilters()
            .Where(cp => cp.TenantId == tenantId
                      && cp.ClientId == clientId
                      && cp.Type == ContactPointType.Phone
                      && cp.ValidTo == null)
            .OrderByDescending(cp => cp.IsPrimary)
            .Select(cp => cp.EncryptedValue)
            .FirstOrDefaultAsync(ct);

        if (encrypted is null) return null;

        // Nullable: Decrypt answers null for a payload it cannot read as well as throwing for one
        // it cannot authenticate, and ToE164 treats both the same way.
        string? clear;

        try
        {
            clear = encryptor.Decrypt(encrypted);
        }
        catch (Exception ex)
        {
            // A KEY problem, not a missing number. Logged as an error and still null to the
            // caller: a message that cannot be addressed must not take a KYC journey down with it.
            logger.LogError(ex,
                "Could not decrypt the telephone of client {ClientId}; no SMS sent.", clientId);
            return null;
        }

        return ToE164(clear, defaultCallingCode);
    }

    /// <summary>
    /// A stored telephone value as an E.164 address, or null when it cannot be one.
    ///
    /// <para>
    /// Internal rather than private so it can be tested directly: this is the one piece of
    /// arithmetic in the SMS path where being wrong means a code going to the wrong person, and it
    /// deserves cases of its own rather than being reached only through a seeded client and a
    /// mocked provider.
    /// </para>
    /// </summary>
    internal static string? ToE164(string? stored, string? defaultCallingCode)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var trimmed = stored.Trim();

        // NormalizePhone already resolves "+225…", "00225…" and the trunk-zero plans into a bare
        // digit string with the indicatif kept — reused rather than re-implemented so there is one
        // answer to "what does this number mean".
        var digits = SensitiveValueNormalizer.NormalizePhone(trimmed);

        if (digits.Length == 0) return null;

        // Whether the stored form declared its country. NormalizePhone drops the plus, so the
        // question has to be asked of the ORIGINAL value — this is the distinction the whole
        // method turns on.
        var wasInternational = trimmed.StartsWith('+')
                            || trimmed.StartsWith("00", StringComparison.Ordinal);

        if (!wasInternational)
        {
            if (string.IsNullOrWhiteSpace(defaultCallingCode)) return null;

            var code = new string([.. defaultCallingCode.Where(char.IsAsciiDigit)]);
            if (code.Length == 0) return null;

            // A locally-dialled number keeps its leading zero in the West African plans this
            // platform serves — Côte d'Ivoire's 0708091801 is ten significant digits — so the
            // code is simply prefixed. The countries that DO have a trunk zero are handled by
            // NormalizePhone, but only for values that arrived in international form; a local
            // number from one of those plans would keep a zero it should lose.
            //
            // Accepted knowingly: this platform's tenants are UEMOA institutions whose plans have
            // no trunk prefix, and the alternative — stripping a zero that is significant in Côte
            // d'Ivoire — corrupts the common case to fix the rare one. A deployment that needs the
            // other behaviour sets its customers' numbers in international form, which is already
            // what the importers encourage.
            digits = code + digits;
        }

        // A plausible E.164: 8 to 15 digits. Shorter is a truncated entry or a short code, longer
        // is not a telephone number — and either way a provider rejects it, at the cost of a
        // billed attempt.
        return digits.Length is >= 8 and <= 15 ? $"+{digits}" : null;
    }

    /// <summary>
    /// A first name to greet by, falling back to the display name — a legal entity has none, and
    /// "Bonjour ," is worse than the full name. Same rule as the email notifier.
    /// </summary>
    private static string FirstNameOrFallback(string? firstName, string displayName)
        => string.IsNullOrWhiteSpace(firstName) ? displayName : firstName.Trim();
}
