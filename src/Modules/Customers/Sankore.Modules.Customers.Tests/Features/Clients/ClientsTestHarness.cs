namespace Sankore.Modules.Customers.Tests.Features.Clients;

using NSubstitute;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Collaborators that only the <c>Clients</c> zone needs, on top of the module-wide
/// <c>TestSupport</c> doubles.
/// </summary>
internal static class ClientsTestHarness
{
    /// <summary>Client number the substituted generator always hands out.</summary>
    public const string MintedClientNumber = "ABJ-2026-000042";

    public const string AgencyCode = "ABJ";

    /// <summary>A directory that resolves one agency and rejects every other one.</summary>
    public static IAgencyDirectory AgencyDirectory(Guid tenantId, Guid agencyId, string agencyCode = AgencyCode)
    {
        var directory = Substitute.For<IAgencyDirectory>();

        // Default for any argument: unknown agency → null → AGENCY_OUT_OF_SCOPE.
        directory.GetAgencyCodeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        directory.GetAgencyCodeAsync(tenantId, agencyId, Arg.Any<CancellationToken>())
            .Returns(agencyCode);

        directory.IsAdvisorEligibleAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        return directory;
    }

    public static IClientNumberGenerator ClientNumbers(string number = MintedClientNumber)
    {
        var generator = Substitute.For<IClientNumberGenerator>();
        generator.NextAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(number);
        return generator;
    }

    /// <summary>The production phonetic key calculator — cheap, pure, and worth exercising.</summary>
    public static IPhoneticKeyCalculator PhoneticKeys() => new WestAfricanPhoneticKeyCalculator();

    /// <summary>
    /// Wraps the reversible test encryptor and COUNTS the calls, which is how the zone
    /// proves its central privacy claim: duplicate detection compares blind indexes and
    /// never decrypts, and a search decrypts at most one value per returned row.
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
}
