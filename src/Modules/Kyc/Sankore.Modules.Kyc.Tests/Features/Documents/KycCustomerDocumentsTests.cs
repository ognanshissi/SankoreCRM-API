namespace Sankore.Modules.Kyc.Tests.Features.Documents;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Kyc;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The images of a customer's KYC files, as the client record's « Documents » tab lists them
/// (product owner, 2026-10-26).
/// </summary>
public sealed class KycCustomerDocumentsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly TestKycDbContextFactory _factory;
    private readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public KycCustomerDocumentsTests() => _factory = new TestKycDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Every_image_of_the_customer_s_files_is_listed_with_its_verdict_and_its_read_route()
    {
        Guid fileId;
        await using (var db = _factory.CreateContext())
        {
            var mine = KycFile.Open(_tenant, _customer, KycChannel.Agency, _agent, _clock);
            var other = KycFile.Open(_tenant, Guid.NewGuid(), KycChannel.Agency, _agent, _clock);
            db.KycFiles.AddRange(mine, other);
            db.KycDocuments.AddRange(
                KycDocument.Register(_tenant, mine.Id, KycDocumentKind.IdentityDocumentFront, "k1.front",
                    "image/jpeg", 1024, new string('a', 64), _agent, _clock),
                KycDocument.Register(_tenant, other.Id, KycDocumentKind.Selfie, "k1.other",
                    "image/jpeg", 1024, new string('b', 64), _agent, _clock));
            await db.SaveChangesAsync();
            fileId = mine.Id;
        }

        var facade = new KycModuleFacade(
            _factory.CreateContext(), new KycSettingsService(_factory.CreateContext(), _clock), _cache,
            NullLogger<KycModuleFacade>.Instance);

        var line = (await facade.ListCustomerDocumentsAsync(_tenant, _customer, CancellationToken.None))
            .Should().ContainSingle().Subject;

        line.Source.Should().Be("Kyc");
        line.Direction.Should().Be(CustomerDocumentDirection.Collected);
        line.Kind.Should().Be(nameof(KycDocumentKind.IdentityDocumentFront));
        line.OriginId.Should().Be(fileId);
        line.ContentPath.Should().Be($"kyc-files/{fileId}/documents/k1.front");
        line.OpenPermissions.Should().Equal(Permissions.CanRevealKycDocumentNumber.Code);
    }
}
