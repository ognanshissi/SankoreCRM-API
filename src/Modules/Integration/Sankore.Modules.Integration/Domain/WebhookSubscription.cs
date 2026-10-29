namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// An address of a partner's system that the institution wants told about lead, project and
/// client events (webhooks lot 2). The signing secret lives in the vault, never here
/// (<c>WebhookSecrets</c>); what is delivered and how it went is lot 3's.
///
/// <para>
/// Its own entity and not an <see cref="IntegrationConnection"/>: a connection is an adapter to a
/// core banking or insurer system, with a health check and a family; a subscription is a URL, a
/// list of event codes and a secret.
/// </para>
/// </summary>
public sealed class WebhookSubscription : AggregateRoot
{
    public const int NameMaxLength = 120;
    public const int UrlMaxLength = 2048;
    public const int DescriptionMaxLength = 500;
    public const int MaxEvents = 50;

    private WebhookSubscription() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string? Description { get; private set; }

    /// <summary>Event codes of <c>WebhookEventCatalog</c>, checked by the caller against it.</summary>
    public List<string> Events { get; private set; } = [];

    public bool IsActive { get; private set; }

    /// <summary>Failed deliveries in a row; reset by a success or by reactivating (lot 3).</summary>
    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }

    /// <summary>The last failure, as a code (<c>HTTP_500</c>, <c>TIMEOUT</c>…) — never the response body.</summary>
    public string? LastFailureCode { get; private set; }

    /// <summary>When the platform switched it off after too many failures; null when off by hand.</summary>
    public DateTimeOffset? DisabledAt { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<WebhookSubscription> Create(
        Guid tenantId, string name, string url, string? description, IEnumerable<string> events,
        Guid actor, DateTimeOffset now)
    {
        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsActive = true,
            CreatedBy = actor,
            CreatedAt = now,
        };

        var applied = subscription.Update(name, url, description, events, actor, now);
        return applied.IsSuccess ? Result.Ok(subscription) : Result.Fail<WebhookSubscription>(applied.Error!);
    }

    /// <summary>
    /// Shape only — length, emptiness, duplicates. Whether the URL may be called and whether each
    /// code exists are the handler's: the first needs the platform's egress policy, the second
    /// the catalogue, neither of which belongs in the entity.
    /// </summary>
    public Result Update(string name, string url, string? description, IEnumerable<string> events, Guid actor, DateTimeOffset now)
    {
        name = name?.Trim() ?? string.Empty;
        url = url?.Trim() ?? string.Empty;
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var codes = (events ?? []).Select(e => e?.Trim() ?? string.Empty).Where(e => e.Length > 0).Distinct().ToList();

        if (name.Length is 0 or > NameMaxLength) return Result.Fail(WebhookErrors.NameInvalid);
        if (url.Length is 0 or > UrlMaxLength) return Result.Fail(WebhookErrors.UrlInvalid);
        if (description is { Length: > DescriptionMaxLength }) return Result.Fail(WebhookErrors.DescriptionInvalid);
        if (codes.Count is 0 or > MaxEvents) return Result.Fail(WebhookErrors.EventsRequired);

        Name = name;
        Url = url;
        Description = description;
        // A new list, so the value comparer sees the column change.
        Events = codes;
        UpdatedBy = actor;
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>
    /// Switches it on or off by hand. Switching on clears the failure streak and the automatic
    /// switch-off: the administrator says the partner is reachable again.
    /// </summary>
    public void SetActive(bool active, Guid actor, DateTimeOffset now)
    {
        IsActive = active;
        if (active)
        {
            ConsecutiveFailures = 0;
            DisabledAt = null;
        }

        UpdatedBy = actor;
        UpdatedAt = now;
    }

    public bool Wants(string eventCode) => IsActive && Events.Contains(eventCode, StringComparer.Ordinal);

    /// <summary>Failed attempts in a row after which the platform switches the subscription off…</summary>
    public const int AutoDisableAfterFailures = 20;

    /// <summary>…provided the partner has accepted nothing for this long either.</summary>
    public static readonly TimeSpan AutoDisableQuietPeriod = TimeSpan.FromHours(24);

    public void RecordSuccess(DateTimeOffset now)
    {
        ConsecutiveFailures = 0;
        LastSuccessAt = now;
    }

    /// <summary>
    /// Counts a failed attempt. Twenty in a row are not enough on their own: a partner down for ten
    /// minutes during a busy hour fails twenty attempts without being dead. The subscription is
    /// switched off only when, on top, nothing has succeeded for <see cref="AutoDisableQuietPeriod"/>.
    /// </summary>
    /// <returns>True when this failure switched it off — the moment to tell somebody.</returns>
    public bool RecordFailure(string? failureCode, DateTimeOffset now)
    {
        ConsecutiveFailures++;
        LastFailureAt = now;
        LastFailureCode = failureCode;

        if (!IsActive
            || ConsecutiveFailures < AutoDisableAfterFailures
            || (LastSuccessAt ?? CreatedAt) > now - AutoDisableQuietPeriod)
            return false;

        IsActive = false;
        DisabledAt = now;
        return true;
    }
}

public static class WebhookErrors
{
    public const string NotFound = "WEBHOOK_NOT_FOUND";
    public const string NameInvalid = "WEBHOOK_NAME_INVALID";
    public const string UrlInvalid = "WEBHOOK_URL_INVALID";
    public const string DescriptionInvalid = "WEBHOOK_DESCRIPTION_INVALID";
    public const string EventsRequired = "WEBHOOK_EVENTS_REQUIRED";
    public const string EventUnknown = "WEBHOOK_EVENT_UNKNOWN";
    public const string EventModuleDisabled = "WEBHOOK_EVENT_MODULE_DISABLED";
    public const string TooMany = "WEBHOOK_TOO_MANY";
    public const string DeliveryNotFound = "WEBHOOK_DELIVERY_NOT_FOUND";
    public const string DeliveryErased = "WEBHOOK_DELIVERY_ERASED";
    public const string SubscriptionInactive = "WEBHOOK_SUBSCRIPTION_INACTIVE";
}
