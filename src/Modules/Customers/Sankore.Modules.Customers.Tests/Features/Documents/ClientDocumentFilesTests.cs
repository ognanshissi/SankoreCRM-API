namespace Sankore.Modules.Customers.Tests.Features.Documents;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Documents.Files;
using Sankore.Modules.Customers.Features.Documents.ListDocuments;
using Sankore.Modules.Customers.Infrastructure.Documents;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// Documents filed straight on the client record (product owner, 2026-10-26): stored encrypted,
/// typed from their bytes, removed logically with a reason, and listed with the rest.
/// </summary>
public sealed class ClientDocumentFilesTests : IDisposable
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 courrier de relance");

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly InMemoryObjectBackend _backend = new();
    private readonly IOptions<ClientDocumentStorageOptions> _options = Options.Create(new ClientDocumentStorageOptions
    {
        EncryptionKey = Convert.ToBase64String(new byte[32]),
    });
    private readonly Guid _clientId;

    public ClientDocumentFilesTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    private ClientDocumentFileHandlers Handlers(Sankore.Modules.Customers.Infrastructure.CustomersDbContext db, bool withStore = true)
    {
        var services = new ServiceCollection();
        if (withStore)
            services.AddSingleton<IClientDocumentStore>(new EncryptedClientDocumentStore(
                _backend, _options, NullLogger<EncryptedClientDocumentStore>.Instance));

        return new ClientDocumentFileHandlers(
            db, TestDoubles.CurrentUser(_tenant, _actor), TestDoubles.AgencyScope(),
            services.BuildServiceProvider(), _options, TimeProvider.System,
            NullLogger<ClientDocumentFileHandlers>.Instance);
    }

    private async Task<Result<Guid>> UploadAsync(byte[]? content = null, string fileName = "relance.pdf", bool withStore = true, string title = "Courrier de relance")
    {
        await using var db = _factory.CreateContext();
        return await Handlers(db, withStore).Handle(new UploadClientDocument(
            _clientId, ClientDocumentKind.Correspondence, title, CustomerDocumentDirection.Delivered,
            new DateOnly(2026, 10, 12), fileName, content ?? Pdf), CancellationToken.None);
    }

    [Fact]
    public async Task A_document_is_stored_encrypted_typed_from_its_bytes_and_read_back_whole()
    {
        var id = (await UploadAsync()).Value;

        _backend.Keys.Should().ContainSingle();
        Convert.ToHexString(_backend.Peek(_backend.Keys.Single())!)
            .Should().NotContain(Convert.ToHexString(Pdf), "the object is ciphertext");

        await using (var db = _factory.CreateContext())
        {
            var row = await db.ClientDocuments.SingleAsync();
            row.ContentType.Should().Be("application/pdf");
            row.Direction.Should().Be(CustomerDocumentDirection.Delivered);
            row.UploadedBy.Should().Be(_actor);
        }

        await using (var db = _factory.CreateContext())
        {
            var content = await Handlers(db).Handle(new GetClientDocumentContentQuery(_clientId, id), CancellationToken.None);
            content.Value.Content.Should().Equal(Pdf);
            content.Value.FileName.Should().Be("relance.pdf");
        }
    }

    [Fact]
    public async Task A_file_whose_bytes_are_not_an_allowed_type_is_refused_whatever_its_name()
    {
        (await UploadAsync(Encoding.ASCII.GetBytes("<html><script>"), "relance.pdf")).Error
            .Should().Be(CustomerErrors.ClientDocumentTypeNotAllowed);
    }

    [Fact]
    public async Task With_no_encryption_key_an_upload_is_refused_rather_than_written_unreadable()
    {
        (await UploadAsync(withStore: false)).Error.Should().Be(CustomerErrors.ClientDocumentsUnavailable);
    }

    [Fact]
    public async Task A_title_is_required()
    {
        (await UploadAsync(title: "  ")).Error.Should().Be(CustomerErrors.ClientDocumentTitleRequired);
    }

    [Fact]
    public async Task An_archived_client_takes_no_new_document()
    {
        await using (var db = _factory.CreateContext())
        {
            var client = await db.Clients.AsTracking().SingleAsync();
            client.Archive("Clôture", _actor).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        (await UploadAsync()).Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task A_removal_needs_a_reason_and_hides_the_document_without_destroying_it()
    {
        var id = (await UploadAsync()).Value;

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(new RemoveClientDocumentCommand(_clientId, id, " "), CancellationToken.None))
                .Error.Should().Be(CustomerErrors.ClientDocumentRemovalReasonRequired);
        }

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(new RemoveClientDocumentCommand(_clientId, id, "Déposé par erreur"), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using (var db = _factory.CreateContext())
        {
            var row = await db.ClientDocuments.SingleAsync();
            row.RemovedBy.Should().Be(_actor);
            row.RemovalReason.Should().Be("Déposé par erreur");
            _backend.Keys.Should().ContainSingle("a logical removal keeps the file");

            (await Handlers(db).Handle(new GetClientDocumentContentQuery(_clientId, id), CancellationToken.None))
                .Error.Should().Be(CustomerErrors.ClientDocumentNotFound);
        }
    }

    [Fact]
    public async Task A_filed_document_is_listed_with_the_others_and_removable_only_with_the_right_to_update()
    {
        var id = (await UploadAsync()).Value;

        async Task<ClientDocumentDto> ListAsync(params string[] permissions)
        {
            await using var db = _factory.CreateContext();
            var user = TestDoubles.CurrentUser(_tenant, _actor);
            user.Permissions.Returns(permissions);
            var handler = new ListClientDocumentsHandler(
                db, user, TestDoubles.AgencyScope(), FixedTenantModules.None,
                NullLogger<ListClientDocumentsHandler>.Instance);
            return (await handler.Handle(new ListClientDocumentsQuery(_clientId), CancellationToken.None)).Value.Items.Single();
        }

        var reader = await ListAsync(Permissions.CanReadCustomer.Code);
        reader.Key.Should().Be($"Client:{id}");
        reader.Kind.Should().Be("ClientDocument.Correspondence");
        reader.Detail.Should().Be("Courrier de relance");
        reader.CanOpen.Should().BeTrue();
        reader.ContentPath.Should().Be($"clients/{_clientId}/documents/{id}/content");
        reader.CanRemove.Should().BeFalse();

        (await ListAsync(Permissions.CanReadCustomer.Code, Permissions.CanUpdateCustomer.Code)).CanRemove.Should().BeTrue();
    }
}
