namespace Sankore.Modules.Kyc.Tests.Features.Duplicates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Duplicates.ClearDuplicateFlag;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// KYC-B-04 — lifting a false positive is a compliance act, not an undo.
/// </summary>
public sealed class ClearDuplicateFlagHandlerTests : IDisposable
{
    private const string Reason = "Jumelles, numéros de CNI consécutifs vérifiés au guichet.";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _officerId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ClearDuplicateFlagHandler _handler;

    public ClearDuplicateFlagHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _handler = new ClearDuplicateFlagHandler(
            _db, _clock, NullLogger<ClearDuplicateFlagHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private async Task<KycFile> SeedAsync(bool flagged = true)
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock);
        if (flagged) file.FlagDuplicateSuspected(_clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private async Task<KycFile> ReloadAsync(Guid id)
        => await _db.KycFiles.AsNoTracking().SingleAsync(f => f.Id == id);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clearing_without_a_reason_is_refused(string reason)
    {
        // Checked in the handler and not only in the validator: an untraceable lift of a
        // compliance flag is the one failure mode this story exists to prevent.
        var file = await SeedAsync();

        var result = await _handler.Handle(
            new ClearDuplicateFlagCommand(file.Id, reason, _officerId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("KYC_DUPLICATE_CLEAR_REASON_REQUIRED");
        (await ReloadAsync(file.Id)).DuplicateSuspected.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_with_a_reason_lifts_the_flag_but_leaves_the_vigilance_High()
    {
        // The file WAS once suspect and that stays true. Lowering the level back would erase the
        // reason the compliance officer joined the circuit, and the next reviewer would see an
        // ordinary file.
        var file = await SeedAsync();

        var result = await _handler.Handle(
            new ClearDuplicateFlagCommand(file.Id, Reason, _officerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var cleared = await ReloadAsync(file.Id);
        cleared.DuplicateSuspected.Should().BeFalse();
        cleared.VigilanceLevel.Should().Be(KycVigilanceLevel.High);
    }

    [Fact]
    public async Task Clearing_a_file_that_was_never_flagged_changes_nothing_and_does_not_fail()
    {
        // Two officers clicking the same alert is the normal case; the second has done nothing
        // wrong and must not be shown an error.
        var file = await SeedAsync(flagged: false);

        var result = await _handler.Handle(
            new ClearDuplicateFlagCommand(file.Id, Reason, _officerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var untouched = await ReloadAsync(file.Id);
        untouched.DuplicateSuspected.Should().BeFalse();
        untouched.VigilanceLevel.Should().Be(KycVigilanceLevel.Standard);
    }

    [Fact]
    public async Task An_unknown_file_is_reported_not_found()
    {
        await SeedAsync();

        var result = await _handler.Handle(
            new ClearDuplicateFlagCommand(Guid.NewGuid(), Reason, _officerId), CancellationToken.None);

        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RAS")]
    public void The_validator_refuses_a_blank_or_token_reason(string reason)
    {
        // "RAS" is the real-world failure: a field that is technically filled proves nothing, so
        // the rule is a minimum length and not merely NotEmpty.
        var result = new ClearDuplicateFlagValidator().Validate(
            new ClearDuplicateFlagCommand(Guid.NewGuid(), reason, Guid.NewGuid()));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ClearDuplicateFlagCommand.Reason));
    }

    [Fact]
    public void The_validator_accepts_a_real_justification()
    {
        var result = new ClearDuplicateFlagValidator().Validate(
            new ClearDuplicateFlagCommand(Guid.NewGuid(), Reason, Guid.NewGuid()));

        result.IsValid.Should().BeTrue();
    }
}
