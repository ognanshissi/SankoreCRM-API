namespace Sankore.Modules.Administration.Features.SmsSettings.GetSmsSettings;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>The current tenant's SMS channel. A query — no <c>ICommand</c>.</summary>
internal sealed record GetSmsSettingsQuery : IRequest<Result<SmsSettingsDto>>;
