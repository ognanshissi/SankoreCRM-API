namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The identifier of a queued write. A consumer module gets one back instead of a result,
/// because the write has not happened yet — and must not have to care whether the external
/// system was reachable (INT-05).
/// </summary>
public readonly record struct IntegrationCommandId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// An identifier owned by the external system. A string and never a Guid: a CBS customer
/// reference is whatever the CBS says it is — digits, a prefixed code, a composite key.
/// </summary>
public readonly record struct ExternalId(string Value)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}

/// <summary>
/// The key that makes a write safe to replay. Deterministic, derived from what the write IS —
/// never from a clock or a Guid — so the same request computed twice collides on
/// <c>ux_integration_commands_tenant_idempotency</c> and the caller gets the existing command
/// back rather than a second one (INT-05).
/// </summary>
public readonly record struct IdempotencyKey(string Value)
{
    public override string ToString() => Value;
}

/// <summary>A calendar month, for the monthly-flow window of the simplified-KYC ceilings.</summary>
public readonly record struct YearMonth(int Year, int Month)
{
    public static YearMonth From(DateOnly date) => new(date.Year, date.Month);

    public DateOnly FirstDay => new(Year, Month, 1);

    public DateOnly LastDay => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}

/// <summary>
/// Answer of <c>CheckHealthAsync</c>. Carries the latency because a CBS that answers in eight
/// seconds is a different operational fact from one that answers in eighty milliseconds, and the
/// activation screen shows it.
/// </summary>
public sealed record IntegrationHealth(
    bool IsHealthy,
    string? Detail,
    TimeSpan? Latency,
    DateTimeOffset CheckedAt)
{
    public static IntegrationHealth Healthy(TimeSpan latency, DateTimeOffset at, string? detail = null)
        => new(true, detail, latency, at);

    public static IntegrationHealth Unhealthy(string detail, DateTimeOffset at, TimeSpan? latency = null)
        => new(false, detail, latency, at);
}
