namespace Sankore.Modules.Kyc.Features.Settings.GetKycSetting;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// One parameter by key. An unknown key answers <c>KYC_SETTING_UNKNOWN</c> rather than an empty
/// payload: a typo in an integration must fail loudly instead of silently resolving to nothing.
/// </summary>
internal sealed record GetKycSettingQuery(string Key) : IRequest<Result<KycSettingDto>>;
