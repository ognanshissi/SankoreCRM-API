namespace Sankore.Modules.Kyc.Tests.Features.Files;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.GetKycFile;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The agency perimeter on reading a KYC file.
///
/// <para>
/// Before this, the read answered for every agency of the tenant: anyone holding <c>kyc:read</c>
/// and a file id saw another branch's compliance file. What is pinned here is that the perimeter is
/// a filter and not a 403 — a refusal would confirm the file exists, which is the one thing the
/// perimeter hides — and that an unresolved agency fails CLOSED rather than becoming everyone's.
/// </para>
/// </summary>
public sealed class GetKycFileAgencyPerimeterTests : IDisposable
{
    private static readonly Guid MyAgency = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgency = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;

    public GetKycFileAgencyPerimeterTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    /// <param name="perimeter">
    /// <c>null</c> is UNRESTRICTED, per IAgencyScopeProvider — a super-user, not a denial. Getting
    /// that backwards would lock every super-user out, so it is exercised explicitly.
    /// </param>
    private GetKycFileHandler Handler(IReadOnlySet<Guid>? perimeter)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_userId);
        currentUser.TenantId.Returns(_tenantId);

        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.GetAccessibleAgencyIdsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(perimeter);

        // The mirrored workflow status is best-effort and irrelevant to the perimeter: the
        // substitute's default failure leaves it null, which is what a file with no mirror reads as.
        return new GetKycFileHandler(_db, currentUser, scope, Substitute.For<IWorkflowModule>());
    }

    private async Task<KycFile> SeedAsync(Guid? agencyId)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock, agencyId: agencyId);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    [Fact]
    public async Task A_file_of_my_agency_is_readable()
    {
        var file = await SeedAsync(MyAgency);

        var result = await Handler(new HashSet<Guid> { MyAgency })
            .Handle(new GetKycFileQuery(file.Id, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(file.Id);
    }

    [Fact]
    public async Task A_file_of_another_agency_is_not_found_rather_than_forbidden()
    {
        var file = await SeedAsync(OtherAgency);

        var result = await Handler(new HashSet<Guid> { MyAgency })
            .Handle(new GetKycFileQuery(file.Id, null), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FileNotFound,
            "a 403 would confirm the file exists, which is exactly what the perimeter hides");
    }

    [Fact]
    public async Task A_file_with_no_agency_is_invisible_to_a_restricted_caller()
    {
        var file = await SeedAsync(agencyId: null);

        var result = await Handler(new HashSet<Guid> { MyAgency })
            .Handle(new GetKycFileQuery(file.Id, null), default);

        result.IsFailure.Should().BeTrue(
            "an unresolved agency must fail closed; treating null as 'everyone's' is the leak");
    }

    [Fact]
    public async Task An_unrestricted_caller_sees_every_agency_including_files_with_none()
    {
        var mine = await SeedAsync(MyAgency);
        var theirs = await SeedAsync(OtherAgency);
        var orphan = await SeedAsync(agencyId: null);

        foreach (var file in new[] { mine, theirs, orphan })
        {
            var result = await Handler(perimeter: null)
                .Handle(new GetKycFileQuery(file.Id, null), default);

            result.IsSuccess.Should().BeTrue(
                "null from IAgencyScopeProvider means unrestricted, never 'sees nothing'");
        }
    }

    [Fact]
    public async Task A_caller_whose_perimeter_is_empty_sees_nothing()
    {
        var file = await SeedAsync(MyAgency);

        var result = await Handler(new HashSet<Guid>())
            .Handle(new GetKycFileQuery(file.Id, null), default);

        result.IsFailure.Should().BeTrue(
            "an empty set is the opposite of null: the user exists and is entitled to no agency");
    }

    [Fact]
    public async Task The_perimeter_applies_to_the_by_customer_read_too()
    {
        var file = await SeedAsync(OtherAgency);

        var result = await Handler(new HashSet<Guid> { MyAgency })
            .Handle(new GetKycFileQuery(null, file.CustomerId), default);

        result.IsFailure.Should().BeTrue(
            "the customer route is the one a screen uses, so leaving it open would leave the leak open");
    }
}
