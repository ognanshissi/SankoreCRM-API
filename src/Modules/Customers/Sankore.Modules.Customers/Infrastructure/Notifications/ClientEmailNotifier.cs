namespace Sankore.Modules.Customers.Infrastructure.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// The one place in this solution that turns a client id into a sent email.
///
/// <para>
/// It exists because the client's address is M01's secret: AES-256-GCM in
/// <c>client_contact_points</c> under this module's key. Other modules legitimately need to mail a
/// customer — M02 sends a remote-collection link and, later, the outcome of their file — and the
/// answer to that is a SEND on the contract (<c>ICustomersModule.NotifyClientAsync</c>), never an
/// address handed over the boundary. The same arrangement M12 settled on for users, where a
/// caller holding an address duplicated the locale fallback and mailed raw JSON when it guessed
/// wrong.
/// </para>
///
/// <para>
/// A service and not code inside the facade so that <c>ClientWelcomeEmailConsumer</c> can share
/// the address resolution it used to carry its own copy of. Two decryption paths for one field is
/// how a primary-address rule starts meaning two different things.
/// </para>
/// </summary>
// Public, not internal: ClientWelcomeEmailConsumer is public because MassTransit instantiates it,
// and a public constructor may not take an internal parameter.
public sealed class ClientEmailNotifier(
    CustomersDbContext db,
    IFieldEncryptor encryptor,
    INotificationsModule notifications,
    ITenantStore tenantStore,
    ILogger<ClientEmailNotifier> logger)
{
    /// <summary>
    /// The client's active email address in clear, or null — preferring the one marked primary and
    /// falling back to any active one.
    ///
    /// <para>
    /// This is the single place the field is decrypted on a notification path, and it decrypts
    /// exactly one value. A value that will not decrypt is a KEY problem and is logged as an error,
    /// but it is still null to the caller: a mail that cannot be addressed must not take a
    /// conversion or a KYC file down with it.
    /// </para>
    /// </summary>
    public async Task<string?> ResolveEmailAsync(
        Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var encrypted = await db.ClientContactPoints
            .IgnoreQueryFilters()
            .Where(cp => cp.TenantId == tenantId
                      && cp.ClientId == clientId
                      && cp.Type == ContactPointType.Email
                      && cp.ValidTo == null)
            .OrderByDescending(cp => cp.IsPrimary)
            .Select(cp => cp.EncryptedValue)
            .FirstOrDefaultAsync(ct);

        if (encrypted is null)
            return null;

        try
        {
            return encryptor.Decrypt(encrypted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not decrypt the email of client {ClientId}; no message sent.", clientId);
            return null;
        }
    }

    /// <summary>
    /// Whether the client can be reached by email at all. A boolean, because that is all a caller
    /// deciding whether to promise a customer a link is entitled to know.
    /// </summary>
    /// <remarks>
    /// It does NOT decrypt: the existence of an active email contact point is enough, and the
    /// question gets asked once per screen load by the agent's remote-link panel. An address that
    /// turns out to be undecryptable surfaces at send time as a failed send, which is the right
    /// place for a key problem to show up — not as "this customer has no email".
    /// </remarks>
    public Task<bool> HasEmailAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => db.ClientContactPoints
            .IgnoreQueryFilters()
            .AnyAsync(
                cp => cp.TenantId == tenantId
                   && cp.ClientId == clientId
                   && cp.Type == ContactPointType.Email
                   && cp.ValidTo == null,
                ct);

    /// <summary>
    /// The client's active email address masked, or null when there is none — the projection an
    /// agent's confirmation dialog shows (KRM-F-07).
    /// </summary>
    /// <remarks>
    /// The same <c>SensitiveValueMasker.MaskEmail</c> M01's own contact-point list already applies,
    /// so this reveals nothing a screen in this product does not already show. It DECRYPTS, which
    /// is why <see cref="HasEmailAsync"/> exists separately for the gate — see the contract.
    /// </remarks>
    public async Task<string?> GetMaskedEmailAsync(
        Guid tenantId, Guid clientId, CancellationToken ct)
        => SensitiveValueMasker.MaskEmail(await ResolveEmailAsync(tenantId, clientId, ct));

    /// <summary>
    /// Queues one templated message to a client. Answers whether it was queued, and never throws —
    /// see the contract for why every caller needs that guarantee.
    /// </summary>
    public async Task<bool> NotifyAsync(NotifyClientRequest request, CancellationToken ct)
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
                    "No message sent for template {TemplateKey}: client {ClientId} does not exist.",
                    request.TemplateKey, request.ClientId);
                return false;
            }

            var email = await ResolveEmailAsync(request.TenantId, request.ClientId, ct);
            if (email is null)
            {
                // Perfectly ordinary, not an incident: a lead captured in the field often has a
                // phone number and nothing else.
                logger.LogInformation(
                    "No message sent for template {TemplateKey}: client {ClientId} has no email.",
                    request.TemplateKey, request.ClientId);
                return false;
            }

            var tenant = await tenantStore.GetAsync(request.TenantId, ct);

            // The client's own preference first, then the tenant's default, then French. The
            // fallback lives HERE and nowhere else — a template locale is matched
            // case-sensitively in PostgreSQL, and a miss renders the message's JSON payload as
            // its body rather than failing.
            var locale = client.PreferredLanguage ?? tenant?.DefaultLanguage ?? "fr";

            var data = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var pair in request.TemplateData ?? new Dictionary<string, object>())
                data[pair.Key] = pair.Value;

            // Filled by this module, after the caller's values, so a caller cannot accidentally
            // address somebody else's customer by name.
            data["full_name"] = client.DisplayName;
            data["client_first_name"] = FirstNameOrFallback(client.FirstName, client.DisplayName);
            data["company_name"] = tenant?.Name ?? string.Empty;

            var queued = await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: request.TemplateKey,
                RecipientEmail: email,
                RecipientName: client.DisplayName,
                Module: "Customers",
                Locale: locale,
                TemplateData: data,
                IdempotencyKey: request.IdempotencyKey,
                TenantId: request.TenantId,
                // So the client record can list what this client was sent (2026-10-26).
                ClientId: request.ClientId), ct);

            if (queued.IsFailure)
            {
                logger.LogWarning(
                    "Could not queue template {TemplateKey} for client {ClientId}: {Error}",
                    request.TemplateKey, request.ClientId, queued.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller's own work has already happened — a KYC file is open, an invitation row
            // is committed. A mail server being down must not undo it.
            logger.LogError(ex,
                "Sending template {TemplateKey} to client {ClientId} threw.",
                request.TemplateKey, request.ClientId);
            return false;
        }
    }

    /// <summary>
    /// A first name to greet the customer by.
    ///
    /// <para>
    /// Falls back to the display name rather than to an empty string: a legal entity has no first
    /// name, and "Bonjour ," in the first message an institution ever sends a customer is worse
    /// than greeting them by their full name.
    /// </para>
    /// </summary>
    private static string FirstNameOrFallback(string? firstName, string displayName)
        => string.IsNullOrWhiteSpace(firstName) ? displayName : firstName.Trim();
}
