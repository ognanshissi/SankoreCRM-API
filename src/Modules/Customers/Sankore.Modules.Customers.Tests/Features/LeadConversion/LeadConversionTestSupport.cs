namespace Sankore.Modules.Customers.Tests.Features.LeadConversion;

using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Doubles specific to the LeadConversion zone, on top of the module-wide ones in
/// <c>TestSupport/</c>.
/// </summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<IIntegrationEvent> _published = [];

    public IReadOnlyList<IIntegrationEvent> Published => _published;

    public IEnumerable<T> OfType<T>() => _published.OfType<T>();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent
    {
        _published.Add(@event);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps the module's fake encryptor and counts the calls, so a test can assert the thing that
/// actually matters about duplicate detection: it is an indexed equality on the blind index and
/// it never unwraps a protected column. A probe that decrypted would have to load and decrypt
/// every client of the tenant — O(n) and a wholesale PII exposure for a question one index
/// answers.
/// </summary>
internal sealed class CountingFieldEncryptor(IFieldEncryptor inner) : IFieldEncryptor
{
    public int EncryptCalls { get; private set; }

    public int DecryptCalls { get; private set; }

    public string? Encrypt(string? plaintext)
    {
        EncryptCalls++;
        return inner.Encrypt(plaintext);
    }

    public string? Decrypt(string? ciphertext)
    {
        DecryptCalls++;
        return inner.Decrypt(ciphertext);
    }
}

internal static class LeadConversionTestDoubles
{
    internal const string AgencyCode = "AG000001";
    internal const string ClientNumber = "AG000001-2026-000042";

    /// <summary>Agency directory resolving every agency to <see cref="AgencyCode"/>.</summary>
    internal static IAgencyDirectory AgencyDirectory(string? agencyCode = AgencyCode)
    {
        var directory = Substitute.For<IAgencyDirectory>();
        directory.GetAgencyCodeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(agencyCode);
        directory.IsAdvisorEligibleAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return directory;
    }

    internal static IClientNumberGenerator ClientNumbers(string clientNumber = ClientNumber)
    {
        var generator = Substitute.For<IClientNumberGenerator>();
        generator.NextAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(clientNumber);
        return generator;
    }

    /// <summary>
    /// The real West-African encoder: this zone must store phonetic keys, and using the real
    /// calculator also proves the handler feeds it the surname first.
    /// </summary>
    internal static IPhoneticKeyCalculator PhoneticKeys() => new WestAfricanPhoneticKeyCalculator();

    internal static async Task SeedLegalFormsAsync(
        CustomersDbContext db, Guid tenantId, params string[] codes)
    {
        var order = 0;
        foreach (var code in codes)
            db.LegalForms.Add(LegalForm.Create(tenantId, code, code, order++));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// An individual client already in the book, used both as the "document already used"
    /// fixture and as the other tenant's record in the isolation test.
    /// </summary>
    internal static Client ExistingIndividual(
        Guid tenantId,
        Guid agencyId,
        Guid createdBy,
        string clientNumber,
        string? documentBlindIndex = null,
        Guid? sourceLeadId = null)
        => Client.CreateIndividual(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: AgencyCode,
            advisorUserId: null,
            firstName: "Awa",
            lastName: "KONE",
            maidenName: null,
            gender: Gender.Female,
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
            docType: IdentityDocumentType.NationalIdCard,
            encryptedDocNumber: documentBlindIndex is null ? null : "enc:whatever",
            docNumberBlindIndex: documentBlindIndex,
            docIssuedOn: null,
            docExpiresOn: null,
            createdBy: createdBy,
            sourceLeadId: sourceLeadId);
}
