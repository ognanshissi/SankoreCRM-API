namespace Sankore.Modules.Kyc.Tests.Features.Verification;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// The score below which a file goes back to the agent instead of entering the approval circuit is
/// a compliance decision — where an IMF draws that line is its own call, not ours. It was a
/// constant in the verification handler; it is now a tenant parameter with the same default.
/// </summary>
public sealed class RejectionFloorTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;

    public RejectionFloorTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private KycSettingsService Service() => new(_factory.CreateContext(), TimeProvider.System);

    [Fact]
    public void The_floor_is_a_declared_tenant_parameter()
        => KycSettingKeys.Find(KycSettingKeys.VerificationRejectionFloor).Should().NotBeNull();

    [Fact]
    public async Task Its_default_is_the_value_that_used_to_be_hard_coded()
    {
        // 40 — changing this default changes who gets refused, for every tenant at once.
        (await Service().GetIntAsync(
            _tenantId, KycSettingKeys.VerificationRejectionFloor, CancellationToken.None))
            .Should().Be(40);
    }

    [Fact]
    public async Task A_tenant_can_move_it()
    {
        var result = await Service().SetAsync(
            _tenantId, KycSettingKeys.VerificationRejectionFloor, "55",
            Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        (await Service().GetIntAsync(
            _tenantId, KycSettingKeys.VerificationRejectionFloor, CancellationToken.None))
            .Should().Be(55);
    }

    [Fact]
    public async Task A_non_numeric_floor_is_refused()
    {
        // It is typed int; a blank or textual value would read back as zero, which would accept
        // every capture however bad.
        (await Service().SetAsync(
            _tenantId, KycSettingKeys.VerificationRejectionFloor, "élevé",
            Guid.NewGuid(), CancellationToken.None))
            .Error.Should().Be(KycErrors.SettingInvalidValue);
    }
}
