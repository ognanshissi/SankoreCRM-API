namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>Which starting data an institution receives — <c>docs/donnees-demarrage-demo.md</c>.</summary>
public enum StartingDataKind
{
    /// <summary>Configuration only, for a real institution: no fictitious person.</summary>
    Starter,

    /// <summary>The starter, plus fictitious accounts and records. Test institutions only.</summary>
    Demo,
}

/// <param name="SystemUserId">The tenant's system account: the author of everything loaded.</param>
public sealed record StartingDataRequest(Guid TenantId, string TenantName, StartingDataKind Kind, Guid SystemUserId)
{
    /// <summary>
    /// What one step tells the next ones — the demo's agencies, accounts and clients by
    /// <see cref="DemoKeys"/> — since the modules cannot read each other's tables. The same request
    /// goes through every step of a load, in order.
    /// </summary>
    public Dictionary<string, Guid> Shared { get; } = new(StringComparer.Ordinal);
}

/// <summary>The names under which demo steps share ids (<see cref="StartingDataRequest.Shared"/>).</summary>
public static class DemoKeys
{
    public static string Agency(string name) => "agency:" + name;
    public static string Account(string profile) => "account:" + profile;
    public static string Client(string key) => "client:" + key;
}

/// <summary>
/// One module's share of a tenant's starting data (owner, 2026-11-08), run by the API's
/// <c>TenantDataLoadJob</c> when the platform asks for it. Each one sends its module's ORDINARY
/// commands, so the data goes through the validation and audit an edit on screen would, and is
/// idempotent: it finds what exists by a natural key (a code, an e-mail, a « DEMO- » reference) and
/// creates only what is missing, so a load run twice creates nothing the second time.
///
/// <para>
/// Called in its own DI scope, under a <c>BackgroundJobContext</c> naming the tenant and its system
/// account, outside any ambient transaction. A <see cref="StartingDataKind.Demo"/> request runs every
/// step a <see cref="StartingDataKind.Starter"/> one does, then the demo's own.
/// </para>
/// </summary>
public interface ITenantStartingData
{
    /// <summary>Lower runs first: agencies before users, products before project templates.</summary>
    int Order { get; }

    /// <summary>Named in a failure, so the platform says which part did not load.</summary>
    string Module { get; }

    /// <summary>What was created, by French label (« Agences » → 2); a label created nothing is left out.</summary>
    Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct);
}
