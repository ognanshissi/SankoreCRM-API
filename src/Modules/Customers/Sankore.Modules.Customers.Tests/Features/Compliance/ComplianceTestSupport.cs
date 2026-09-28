namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// A clock the test drives.
/// <para>
/// Retention is measured in years, and <c>Client.Archive()</c> stamps <c>ArchivedAt</c> with the
/// real <c>UtcNow</c> (the aggregate takes no clock). So rather than reaching into a private
/// setter to backdate an archive, these tests move the READER's clock ten-plus years forward:
/// the cut-off then lands after the archive date and the client is genuinely eligible, with no
/// reflection and no domain change.
/// </para>
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory <see cref="IFileStore"/>: keeps what was stored so a test can read it back.</summary>
internal sealed class InMemoryFileStore : IFileStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <summary>Set to make <see cref="ReadAsync"/> behave as if the blob had been swept away.</summary>
    public bool SimulateMissingFile { get; set; }

    public Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        var reference = $"customers/{Guid.NewGuid():N}/{originalFileName}";
        _files[reference] = buffer.ToArray();

        return Task.FromResult(reference);
    }

    public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
    {
        if (SimulateMissingFile || !_files.TryGetValue(fileReference, out var bytes))
            throw new IOException($"'{fileReference}' not found.");

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task DeleteAsync(string fileReference, CancellationToken ct)
    {
        _files.Remove(fileReference);
        return Task.CompletedTask;
    }

    /// <summary>Content of the single stored file, decoded as UTF-8 (BOM stripped).</summary>
    public string SingleFileAsText()
    {
        var bytes = _files.Values.Single();
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        return text.TrimStart('﻿');
    }
}

/// <summary>Builders for the aggregates these tests need, kept out of the test bodies.</summary>
internal static class ComplianceTestData
{
    internal const string AgencyCode = "AG1";

    /// <summary>
    /// A <c>PendingKyc</c> individual with an encrypted identity document and, optionally, a
    /// primary phone. Encryption goes through the same <see cref="IFieldEncryptor"/> the handlers
    /// use, so the masking assertions exercise the real decrypt-then-mask path.
    /// </summary>
    internal static Client Individual(
        Guid tenantId,
        Guid agencyId,
        string clientNumber,
        string firstName,
        string lastName,
        Guid createdBy,
        IFieldEncryptor encryptor,
        IBlindIndexer indexer,
        string? documentNumber = null,
        string? phoneNumber = null,
        string agencyCode = AgencyCode)
    {
        var client = Client.CreateIndividual(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: agencyCode,
            advisorUserId: null,
            firstName: firstName,
            lastName: lastName,
            maidenName: null,
            gender: Gender.Male,
            encryptedDateOfBirth: null,
            dateOfBirthBlindIndex: null,
            birthPlace: null,
            nationality: "CI",
            maritalStatus: null,
            fatherName: null,
            motherName: null,
            profession: null,
            employer: null,
            encryptedDeclaredIncome: null,
            declaredIncomeCurrency: null,
            preferredLanguage: "fr",
            docType: documentNumber is null ? null : IdentityDocumentType.NationalIdCard,
            encryptedDocNumber: documentNumber is null ? null : encryptor.Encrypt(documentNumber),
            docNumberBlindIndex: documentNumber is null
                ? null
                : indexer.Compute(
                    BlindIndexPurpose.IdentityDocument,
                    SensitiveValueNormalizer.NormalizeDocumentNumber(documentNumber)),
            docIssuedOn: null,
            docExpiresOn: null,
            createdBy: createdBy);

        if (phoneNumber is not null)
        {
            client.AddContactPoint(
                type: ContactPointType.Phone,
                encryptedValue: encryptor.Encrypt(phoneNumber)!,
                blindIndex: indexer.Compute(
                    BlindIndexPurpose.Phone, SensitiveValueNormalizer.NormalizePhone(phoneNumber)),
                label: "mobile",
                isPrimary: true,
                validFrom: DateTimeOffset.UtcNow,
                actor: createdBy);
        }

        return client;
    }

    /// <summary>Same client, archived — the only state anonymization accepts.</summary>
    internal static Client ArchivedIndividual(
        Guid tenantId,
        Guid agencyId,
        string clientNumber,
        Guid createdBy,
        IFieldEncryptor encryptor,
        IBlindIndexer indexer,
        string firstName = "Awa",
        string lastName = "Kone",
        string? documentNumber = "CI0123456789",
        string? phoneNumber = "+22507112233",
        string agencyCode = AgencyCode)
    {
        var client = Individual(
            tenantId, agencyId, clientNumber, firstName, lastName, createdBy,
            encryptor, indexer, documentNumber, phoneNumber, agencyCode);

        client.Archive("Client file closed at the customer's request.", createdBy).IsSuccess
            .Should().BeTrue();

        return client;
    }

    /// <summary>
    /// A container wired the way the Hangfire jobs expect it: they resolve everything from an
    /// <see cref="IServiceScopeFactory"/> scope, so testing them means giving them a real one.
    /// The DbContext factory is shared, so the job sees the rows the test wrote.
    /// </summary>
    internal static ServiceProvider JobServices(
        Func<CustomersDbContext> contextFactory,
        TimeProvider clock,
        ICustomerSettings? settings = null,
        IKycModule? kyc = null,
        IFileStore? fileStore = null,
        IAgencyScopeProvider? agencyScope = null,
        IBlindIndexer? indexer = null,
        IFieldEncryptor? encryptor = null)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => contextFactory());
        services.AddSingleton(clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        // The real projector: idempotency and the no-sensitive-data guard are part of what the
        // retention job has to satisfy, so substituting it would test nothing.
        services.AddScoped<IClientTimelineProjector, ClientTimelineProjector>();

        if (settings is not null) services.AddSingleton(settings);
        if (kyc is not null) services.AddSingleton(kyc);
        if (fileStore is not null) services.AddSingleton(fileStore);
        if (agencyScope is not null) services.AddSingleton(agencyScope);
        if (indexer is not null) services.AddSingleton(indexer);
        if (encryptor is not null) services.AddSingleton(encryptor);

        return services.BuildServiceProvider();
    }
}
