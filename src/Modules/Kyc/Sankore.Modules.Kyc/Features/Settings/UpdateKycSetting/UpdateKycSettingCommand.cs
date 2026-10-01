namespace Sankore.Modules.Kyc.Features.Settings.UpdateKycSetting;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes one M02 parameter.
///
/// <para>
/// A mutation, so <see cref="ICommand"/> (transaction + audit) and <see cref="IResourceCommand"/>.
/// The audit entry is the point as much as the write: these values move a compliance ceiling, the
/// length of an approval ladder and the periodicity of a review for the entire tenant. "Who lowered
/// the rejection floor to 5, and when" is the first question asked after a file that should not have
/// passed, and nothing else in the system records it.
/// </para>
///
/// <para>
/// No property is marked sensitive: thresholds and periodicities are not personal data, and the
/// audit trail deliberately keeps the value in clear — a masked audit entry would answer the
/// question above with "a value was changed to ***".
/// </para>
/// </summary>
internal sealed record UpdateKycSettingCommand(string Key, string Value)
    : IRequest<Result<KycSettingDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycSetting";
    public string? ResourceId => Key;
}

/// <summary>The key travels in the route, so the body carries the value alone.</summary>
internal sealed record UpdateKycSettingRequest(string Value);
