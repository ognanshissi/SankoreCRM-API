namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using System.Text;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// The props of the mappings suite. Everything is fixed: a <c>Guid.NewGuid()</c> in a fixture is
/// the one thing that would differ between two runs.
/// </summary>
internal static class MappingsTestFixtures
{
    internal static readonly Guid Actor = new("55555555-5555-5555-5555-555555555555");

    /// <summary>
    /// A persisted connection for the given tenant. <see cref="FakeSettings"/> because it is the
    /// only settings record the domain accepts on a <see cref="IntegrationKind.Fake"/> row, and
    /// the kind is irrelevant to a mapping table.
    /// </summary>
    internal static IntegrationConnection Connection(Guid tenantId, Guid? id = null)
        => IntegrationConnection.Create(
            tenantId: tenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Double local",
            settings: new FakeSettings(),
            createdBy: Actor,
            clock: TimeProvider.System,
            id: id ?? new Guid("11111111-1111-1111-1111-111111111111"));

    internal static IntegrationMapping Mapping(
        Guid tenantId,
        Guid connectionId,
        MappingDomain domain,
        string crmCode,
        string externalCode,
        string? label = null)
        => IntegrationMapping.Create(
            tenantId, connectionId, domain, crmCode, externalCode, Actor, TimeProvider.System, label);

    /// <summary>A CSV body with the BOM an operator's Excel adds when it saves the export back.</summary>
    internal static byte[] WithBom(string csv)
        => [.. new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble(),
            .. Encoding.UTF8.GetBytes(csv)];
}

/// <summary>
/// A clock the test moves by hand, so "UpdatedAt changed" is an assertion and not a race with the
/// system clock's resolution.
/// </summary>
internal sealed class MappingsTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public static MappingsTestClock At(int year, int month, int day)
        => new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>
/// An <see cref="IFileStore"/> in a dictionary. It also RECORDS deletions, which is the only way
/// to assert that the import deletes its upload in a <c>finally</c> — including on the failure
/// paths, where a leaked file is invisible.
/// </summary>
internal sealed class MappingsMemoryFileStore : IFileStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    internal List<string> Deleted { get; } = [];

    internal bool Holds(string reference) => _files.ContainsKey(reference);

    /// <summary>Seeds a file directly, as if it had already been uploaded.</summary>
    internal string Seed(string csv, string fileName = "mappings.csv")
        => Seed(System.Text.Encoding.UTF8.GetBytes(csv), fileName);

    internal string Seed(byte[] content, string fileName = "mappings.csv")
    {
        var reference = $"{Guid.NewGuid():N}/{fileName}";
        _files[reference] = content;
        return reference;
    }

    public async Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return Seed(buffer.ToArray(), originalFileName);
    }

    public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
        => _files.TryGetValue(fileReference, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(fileReference);

    public Task DeleteAsync(string fileReference, CancellationToken ct)
    {
        Deleted.Add(fileReference);
        _files.Remove(fileReference);
        return Task.CompletedTask;
    }
}

/// <summary>The smallest <see cref="ICurrentUser"/> that answers an id.</summary>
internal sealed class MappingsStubCurrentUser(Guid id) : ICurrentUser
{
    public Guid Id { get; } = id;

    public Guid TenantId { get; init; }

    public string DisplayName => "Test operator";

    public bool IsAuthenticated => true;

    public IReadOnlyList<string> Roles => ["Administrator"];
}
