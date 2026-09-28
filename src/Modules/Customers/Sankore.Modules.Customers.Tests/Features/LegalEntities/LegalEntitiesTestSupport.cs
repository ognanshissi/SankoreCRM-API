namespace Sankore.Modules.Customers.Tests.Features.LegalEntities;

using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Doubles specific to the LegalEntities zone, on top of the module-wide ones in
/// <c>TestSupport/</c>. The publisher records instead of sending so the tests can assert
/// on the outbox, and the phonetic calculator is a substitute: this zone asserts that a
/// key is STORED, not how the West-African encoder computes it (that has its own tests).
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

internal static class LegalEntitiesTestDoubles
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

    internal static IPhoneticKeyCalculator PhoneticKeys(string key = "KSN")
    {
        var calculator = Substitute.For<IPhoneticKeyCalculator>();
        calculator.Compute(Arg.Any<string?>()).Returns(key);
        return calculator;
    }

    /// <summary>Seeds the tenant's active legal-form list.</summary>
    internal static async Task SeedLegalFormsAsync(
        CustomersDbContext db, Guid tenantId, params string[] codes)
    {
        var order = 0;
        foreach (var code in codes)
            db.LegalForms.Add(LegalForm.Create(tenantId, code, code, order++));

        await db.SaveChangesAsync();
    }

    /// <summary>A legal client already in the base, used as the target of a declaration.</summary>
    internal static Client LegalClient(
        Guid tenantId,
        Guid agencyId,
        Guid createdBy,
        string legalName = "SANKORE DISTRIBUTION",
        string legalFormCode = "SARL",
        string? encryptedRegistrationNumber = null,
        string? registrationNumberBlindIndex = null,
        string clientNumber = ClientNumber)
        => Client.CreateLegal(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: AgencyCode,
            advisorUserId: null,
            legalName: legalName,
            legalFormCode: legalFormCode,
            encryptedRegistrationNumber: encryptedRegistrationNumber,
            registrationNumberBlindIndex: registrationNumberBlindIndex,
            encryptedTaxIdNumber: null,
            incorporationDate: new DateOnly(2019, 4, 12),
            preferredLanguage: "fr",
            createdBy: createdBy);

    /// <summary>An individual client — the CLIENT_NOT_LEGAL_ENTITY fixture.</summary>
    internal static Client IndividualClient(
        Guid tenantId, Guid agencyId, Guid createdBy, string clientNumber = "AG000001-2026-000099")
        => Client.CreateIndividual(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: AgencyCode,
            advisorUserId: null,
            firstName: "Adjoua",
            lastName: "KOUASSI",
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
            encryptedDocNumber: null,
            docNumberBlindIndex: null,
            docIssuedOn: null,
            docExpiresOn: null,
            createdBy: createdBy);
}
