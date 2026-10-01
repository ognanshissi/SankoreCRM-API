namespace Sankore.Modules.Kyc.Features.Settings.ListKycSettings;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Every M02 parameter of the caller's tenant. A query — no <c>ICommand</c>, so neither the
/// transaction nor the audit behaviour runs.
/// </summary>
internal sealed record ListKycSettingsQuery : IRequest<Result<IReadOnlyList<KycSettingDto>>>;
